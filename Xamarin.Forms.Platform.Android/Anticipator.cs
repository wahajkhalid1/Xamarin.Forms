using System;
using System.Collections.Generic;
using System.Reflection;
using Android.Content;
using Android.Views;
using Android.Util;
using Android.App;
using Xamarin.Forms.Core.Internals;
using FLabelRenderer = Xamarin.Forms.Platform.Android.FastRenderers.LabelRenderer;
using ABuildVersionCodes = Android.OS.BuildVersionCodes;
using ABuild = Android.OS.Build;
using AView = Android.Views.View;
using ARelativeLayout = Android.Widget.RelativeLayout;
using AToolbar = Android.Support.V7.Widget.Toolbar;

namespace Xamarin.Forms.Platform.Android
{
	/// <summary>
	/// Computing the results that need to be cached often require calling into the Android OS. 
	/// Calling into the Android OS off the UIThread is a "gray" operation. E.g. we know not to 
	/// update UI elements off the UIThread, but what about getting the SdkInt version? Likely
	/// ok but just the same we want to track ALL Android OS APIs we call off the UIThread.
	/// 
	/// All Android OS calls made off the UIThread can be found in the implementations of 
	/// IAnticipatedValue.Get. Calls off the UIThread into the Android OS should not exist
	/// elsewhere. 
	/// </summary>
	public class AndroidAnticipator : Anticipator
	{
		static class Key
		{
			internal struct SdkVersion : IAnticipatable
			{
				object IAnticipatable.Get()
					=> ABuild.VERSION.SdkInt;

				public override string ToString()
					=> $"{nameof(SdkVersion)}";
			}

			internal struct IdedResourceExists : IAnticipatable
			{
				readonly internal Context Context;
				readonly internal int Id;

				internal IdedResourceExists(Context context, int id)
				{
					Context = context;
					Id = id;
				}

				object IAnticipatable.Get()
				{
					if (Id == 0)
						return false;

					using (var value = new TypedValue())
						return Context.Theme.ResolveAttribute(Id, value, true);
				}

				public override string ToString()
					=> $"{nameof(IdedResourceExists)}, id={ResourceName(Id)}";
			}

			internal struct NamedResourceExists : IAnticipatable
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

				object IAnticipatable.Get()
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

			internal struct InflateResource : IAnticipatable
			{
				readonly internal Context Context;
				readonly internal int Id;

				internal InflateResource(Context context, int id)
				{
					Context = context;
					Id = id;
				}

				object IAnticipatable.Get()
				{
					if (Id == 0)
						return null;

					var activity = (Activity)Context;

					var layoutInflator = activity.LayoutInflater;
					return layoutInflator.Inflate(Id, null);
				}

				public override string ToString()
					=> $"{nameof(InflateResource)}, id={ResourceName(Id)}";
			}

			internal struct InflateIdedResourceFromContext : IAnticipatable
			{
				readonly internal Context Context;
				readonly internal int Id;

				internal InflateIdedResourceFromContext(Context context, int id)
				{
					Context = context;
					Id = id;
				}

				object IAnticipatable.Get()
				{
					if (Id == 0)
						return null;

					var layoutInflator = LayoutInflater.FromContext(Context);
					return layoutInflator.Inflate(Id, null);
				}

				public override string ToString()
					=> $"{nameof(InflateResource)}, id={ResourceName(Id)}";
			}

			internal struct ActivateView :
				IAnticipatable, IEquatable<ActivateView>
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

				object IAnticipatable.Get()
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
		}

		public static void Initialize(ContextWrapper context)
		{
			s_singleton = new AndroidAnticipator(context);
		}

		static internal ABuildVersionCodes SdkVersion
			=> (ABuildVersionCodes)s_singleton.Get(new Key.SdkVersion());

		static internal bool IdedResourceExists(Context context, int id)
			=> (bool)s_singleton.Get(new Key.IdedResourceExists(context, id));

		static internal bool NamedResourceExists(Context context, string name, string type)
			=> (bool)s_singleton.Get(new Key.NamedResourceExists(context, name, type));

		static internal AView InflateResource(Context context, int id)
			=> (AView)s_singleton.Get(new Key.InflateResource(context, id));

		static internal AView ActivateView(Context context, Type type)
			=> (AView)s_singleton.Get(new Key.ActivateView(context, type));

		static string ResourceName(int id)
			=> id != 0 && s_resourceNames.TryGetValue(id, out var name) ? name : id.ToString();

		static Dictionary<int, string> s_resourceNames = new Dictionary<int, string>
		{
			[FormsAppCompatActivity.ToolbarResource] = nameof(FormsAppCompatActivity.ToolbarResource),
			[global::Android.Resource.Attribute.ColorAccent] = nameof(global::Android.Resource.Attribute.ColorAccent),
			[Resource.Layout.FlyoutContent] = nameof(Resource.Layout.FlyoutContent),
		};

		static AndroidAnticipator s_singleton;

		public AndroidAnticipator(ContextWrapper context)
		{
			if (context == null)
				throw new ArgumentNullException(nameof(context));

			Anticipate(new Key.SdkVersion());

			Anticipate(new ClassConstruction(typeof(Resource.Layout)));
			Anticipate(new ClassConstruction(typeof(Resource.Attribute)));

			Anticipate(new Key.ActivateView(context, typeof(AToolbar), o => new AToolbar(o)));
			Anticipate(new Key.ActivateView(context.BaseContext, typeof(ARelativeLayout), o => new ARelativeLayout(o)));
			Anticipate(new Key.InflateResource(context, FormsAppCompatActivity.ToolbarResource));

			Anticipate(new Key.IdedResourceExists(context, global::Android.Resource.Attribute.ColorAccent));
			Anticipate(new Key.NamedResourceExists(context, "colorAccent", "attr"));

			Anticipate(new Key.InflateIdedResourceFromContext(context, Resource.Layout.FlyoutContent));

			Anticipate(new Key.ActivateView(context, typeof(FLabelRenderer)));
			Anticipate(new Key.ActivateView(context, typeof(PageRenderer)));

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

		protected override object Activate(Type type, params object[] arguments)
		{
			return base.Activate(type, arguments);

			//object result = null;

			//if (type == typeof(FLabelRenderer))
			//	result = new FLabelRenderer((Context)arguments[0]);

			//if (type == typeof(PageRenderer))
			//	result = new PageRenderer((Context)arguments[0]);

			//if (type == typeof(ListViewRenderer))
			//	result = new ListViewRenderer((Context)arguments[0]);

			//var hitOrMiss = result == null ? "MISS" : "HIT";

			//if (result == null)
			//	result = base.Activate(type, arguments);

			//Log("ACTIVATOR {0}: {1}", hitOrMiss, type.Name);
			//return result;
		}
	}
}