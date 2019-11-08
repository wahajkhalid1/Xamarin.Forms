using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Threading;
using Android.Graphics.Drawables;
using Android.Graphics;
using Android.Content;
using Xamarin.Forms.Internals;
using Android.Views;
using Android.Util;
using Android.App;
using ALog = Android.Util.Log;
using System.Diagnostics;
using ABuildVersionCodes = Android.OS.BuildVersionCodes;
using ABuild = Android.OS.Build;
using AView = Android.Views.View;
using ARelativeLayout = Android.Widget.RelativeLayout;
using FLabelRenderer = Xamarin.Forms.Platform.Android.FastRenderers.LabelRenderer;
using FButtonRenderer = Xamarin.Forms.Platform.Android.FastRenderers.ButtonRenderer;
using FImageRenderer = Xamarin.Forms.Platform.Android.FastRenderers.ImageRenderer;
using FFrameRenderer = Xamarin.Forms.Platform.Android.FastRenderers.FrameRenderer;
using AFragment = Android.Support.V4.App.Fragment;
using AToolbar = Android.Support.V7.Widget.Toolbar;
using System.Linq;

namespace Xamarin.Forms.Platform.Android
{
	/// <summary>
	/// 
	/// Anticipator is a hand-rolled threadpool that exists to speedup startup by activating
	/// threads that can be used to race ahead of the UIThread in order to compute and cache results 
	/// that the UIThread would otherwise have to compute as part of startup. This requires
	/// making some startup code re-entrant. 
	/// 
	/// So developers don't need to wonder if the startup code they're writing is re-entrant we 
	/// isolate all re-entrant code here in the static members of Anticipator.
	/// 
	/// Computing the results that need to be cached often require calling into the Android OS. 
	/// Calling into the Android OS off the UIThread is a "gray" operation. E.g. we know not to 
	/// update UI elements off the UIThread, but what about getting the SdkInt version? Likely
	/// ok but just the same we want to track ALL Android OS APIs we call off the UIThread.
	/// Isolating all re-entrant code in static Anticipator members simplifies accounting of
	/// all Android OS calls potentially made off the UIThread.
	/// 
	/// </summary>
	public class Anticipator
	{
		sealed class ThreadPool
		{
			const int ThreadLifeTimeSeconds = 5;
			readonly static TimeSpan LoopTimeOut = TimeSpan.FromSeconds(ThreadLifeTimeSeconds);
			readonly static int Concurrency = 1; //Environment.ProcessorCount;

			readonly Thread[] _threads;
			readonly AutoResetEvent[] _signals;
			readonly ConcurrentQueue<Action> _actions;
			readonly Action _cleanup;
			int _threadCount = Concurrency;

			internal ThreadPool(Action cleanup)
			{
				_cleanup = cleanup;
				_actions = new ConcurrentQueue<Action>();

				_threads = new Thread[Concurrency];
				_signals = new AutoResetEvent[Concurrency];

				_signals[0] = new AutoResetEvent(true);
				_threads[0] = new Thread(Loop);
				_threads[0].Start(_signals[0]);

				if (Concurrency > 1)
				{
					Schedule(() =>
					{
						for (var i = 1; i < _threads.Length; i++)
						{
							_signals[i] = new AutoResetEvent(true);
							_threads[i] = new Thread(Loop);
							_threads[i].Start(_signals[i]);
						}
					});
				}
			}

			void Loop(object argument)
			{
				var signal = (AutoResetEvent)argument;

				while (signal.WaitOne(LoopTimeOut))
				{
					// process actions
					while (_actions.Count > 0)
					{
						if (!_actions.TryDequeue(out Action action))
							continue; // lost race to dequeue

						action();
					}
				}

				Interlocked.Decrement(ref _threadCount);
				if (_threadCount == 0)
					_cleanup();
			}

			void Signal()
			{
				for (var i = 0; i < _signals.Length; i++)
				{
					if (_signals[i] != null)
						_signals[i].Set();
				}
			}

			internal void Schedule(Action action)
			{
				if (action == null)
					throw new ArgumentNullException(nameof(action));

				_actions.Enqueue(action);

				Signal();
			}
		}

		class DummyDrawable : Drawable
		{
			public override int Opacity => 0;
			public override void Draw(Canvas canvas) { }
			public override void SetAlpha(int alpha) { }
			public override void SetColorFilter(ColorFilter colorFilter) { }
		}

		interface IAnticipatedValue
		{
			object Get();
		}

		struct ClassConstruction : IAnticipatedValue
		{
			Type _type;

			internal ClassConstruction(Type type)
			{
				_type = type;
			}

			object IAnticipatedValue.Get()
			{
				RuntimeHelpers.RunClassConstructor(_type.TypeHandle);
				return null;
			}

			public override string ToString()
				=> $".cctor={_type.Name}";
		}

		struct SdkVersion : IAnticipatedValue
		{
			object IAnticipatedValue.Get()
				=> ABuild.VERSION.SdkInt;

			public override string ToString()
				=> $"{nameof(SdkVersion)}";
		}

		struct IdedResourceExists : IAnticipatedValue
		{
			readonly internal Context Context;
			readonly internal int Id;

			internal IdedResourceExists(Context context, int id)
			{
				Context = context;
				Id = id;
			}

			object IAnticipatedValue.Get()
			{
				if (Id == 0)
					return false;

				using (var value = new TypedValue())
					return Context.Theme.ResolveAttribute(Id, value, true);
			}

			public override string ToString()
				=> $"{nameof(IdedResourceExists)}, id={ResourceName(Id)}";
		}

		struct NamedResourceExists : IAnticipatedValue
		{
			readonly internal Context Context;
			readonly internal string Name;
			readonly internal string Type;

			internal NamedResourceExists(Context context, string name, string type)
			{
				Context = context;
				Name = name;
				Type = type;
			}

			object IAnticipatedValue.Get()
			{
				var id = Context.Resources.GetIdentifier(Name, Type, Context.PackageName);
				if (id == 0)
					return false;

				using (var value = new TypedValue())
					return Context.Theme.ResolveAttribute(id, value, true);
			}

			public override string ToString()
				=> $"{nameof(NamedResourceExists)}, name='{Name}', type='{Type}'";
		}

		struct InflateIdedResource : IAnticipatedValue
		{
			readonly internal Context Context;
			readonly internal int Id;

			internal InflateIdedResource(Context context, int id)
			{
				Context = context;
				Id = id;
			}

			object IAnticipatedValue.Get()
			{
				if (Id == 0)
					return null;

				var activity = (Activity)Context;

				var layoutInflator = activity.LayoutInflater;
				return layoutInflator.Inflate(Id, null);
			}

			public override string ToString()
				=> $"{nameof(InflateIdedResource)}, id={ResourceName(Id)}";
		}

		struct InflateIdedResourceFromContext : IAnticipatedValue
		{
			readonly internal Context Context;
			readonly internal int Id;

			internal InflateIdedResourceFromContext(Context context, int id)
			{
				Context = context;
				Id = id;
			}

			object IAnticipatedValue.Get()
			{
				if (Id == 0)
					return null;

				var layoutInflator = LayoutInflater.FromContext(Context);
				return layoutInflator.Inflate(Id, null);
			}

			public override string ToString()
				=> $"{nameof(InflateIdedResource)}, id={ResourceName(Id)}";
		}

		struct ActivateView :
			IAnticipatedValue, IEquatable<ActivateView>
		{
			readonly internal Context Context;
			readonly internal Type Type;
			readonly internal Func<Context, object> Factory;

			internal ActivateView(Context context, Type type, Func<Context, object> activator = null)
			{
				Context = context;
				Type = type;
				Factory = activator;
			}

			object IAnticipatedValue.Get()
			{
				if (Factory == null)
					return Activator.CreateInstance(Type, Context);

				return Factory(Context);
			}

			public override int GetHashCode()
				=> Context.GetHashCode() ^ Type.GetHashCode();
			public bool Equals(ActivateView other)
				=> other.Context == Context && other.Type == Type;
			public override bool Equals(object other)
				=> other is ActivateView ? Equals((ActivateView)other) : false;
			public override string ToString()
				=> $"{nameof(ActivateView)}, Type={Type.GetTypeInfo().Name}";
		}

		static ThreadPool s_threadPool;
		static ConcurrentDictionary<object, object> s_cache;
		static Dictionary<int, string> s_resourceNames = new Dictionary<int, string>
		{
			[FormsAppCompatActivity.ToolbarResource] = nameof(FormsAppCompatActivity.ToolbarResource),
			[global::Android.Resource.Attribute.ColorAccent] = nameof(global::Android.Resource.Attribute.ColorAccent),
			[Resource.Layout.FlyoutContent] = nameof(Resource.Layout.FlyoutContent),
		};

		public static void Initialize(ContextWrapper context)
		{
			if (context == null)
				throw new ArgumentNullException(nameof(context));

			s_threadPool = new ThreadPool(() => s_cache.Clear());
			s_cache = new ConcurrentDictionary<object, object>();

			Anticipate(new SdkVersion());

			Anticipate(new ClassConstruction(typeof(Resource.Layout)));
			Anticipate(new ClassConstruction(typeof(Resource.Attribute)));

			Anticipate(new ActivateView(context, typeof(AToolbar), o => new AToolbar(o)));
			Anticipate(new ActivateView(context.BaseContext, typeof(ARelativeLayout), o => new ARelativeLayout(o)));
			Anticipate(new InflateIdedResource(context, FormsAppCompatActivity.ToolbarResource));

			Anticipate(new IdedResourceExists(context, global::Android.Resource.Attribute.ColorAccent));
			Anticipate(new NamedResourceExists(context, "colorAccent", "attr"));

			Anticipate(new InflateIdedResourceFromContext(context, Resource.Layout.FlyoutContent));

			//s_threadPool.Schedule(() => {
			//	new PageRenderer(s_context);
			//	new FLabelRenderer(s_context);
			//	new FButtonRenderer(s_context);
			//	new FImageRenderer(s_context);
			//	new FFrameRenderer(s_context);
			//	new ListViewRenderer(s_context);
			//	new AFragment();
			//	new DummyDrawable();
			//});
		}

		static string ResourceName(int id)
			=> id != 0 && s_resourceNames.TryGetValue(id, out var name) ? name : id.ToString();

		static void Log(string format, params object[] arguments)
			=> Profile.WriteLog(format, arguments);

		static object Get<T>(T key = default)
			where T : IAnticipatedValue
		{
			if (!s_cache.TryGetValue(key, out var value))
			{
				Log("MISS: {0}", key);
				return key.Get();
			}

			if (!(key is SdkVersion))
				Log("HIT: {0}", key);

			return value;
		}

		static void Anticipate<T>(T key = default)
			where T : IAnticipatedValue
		{
			s_threadPool.Schedule(() => {
				try
				{
					var stopwatch = new Stopwatch();
					stopwatch.Start();
					s_cache.TryAdd(key, key.Get());

					Log("CASHED: {0}, ms={1}", key, 
						TimeSpan.FromTicks(stopwatch.ElapsedTicks).Milliseconds);
				}
				catch (Exception e)
				{
					Log("EXCEPTION: {0}: {1}", key, e);
				}
			});
		}

		static internal ABuildVersionCodes SdkInt
			=> (ABuildVersionCodes)Get(new SdkVersion());

		static internal bool HasResource(Context context, int id)
			=> (bool)Get(new IdedResourceExists(context, id));

		static internal bool HasResource(Context context, string name, string type)
			=> (bool)Get(new NamedResourceExists(context, name, type));

		static internal AView InflateResource(Context context, int id)
			=> (AView)Get(new InflateIdedResource(context, id));

		static internal AView Activate(Context context, Type type)
			=> (AView)Get(new ActivateView(context, type));
	}
}