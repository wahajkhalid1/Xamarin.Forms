using System;
#if NETSTANDARD2_0
using System.Collections.Concurrent;
using System.Threading;
#endif
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xamarin.Forms.Internals;
using System.Collections.Generic;
using System.Collections;

namespace Xamarin.Forms.Core.Internals
{
	public interface IAnticipatable
	{
		object Get();
	}

	public abstract class Anticipator
	{
		interface IHeapOrCache : IDisposable
		{
			void Set(object key, object value);
			bool TryGet(object key, out object value);
		}

#if NETSTANDARD2_0
		sealed class HeapOrCache : IDisposable
		{
			sealed class Cache : IHeapOrCache
			{
				readonly ConcurrentDictionary<object, object> _dictionary
					= new ConcurrentDictionary<object, object>();

				public void Set(object key, object value)
					=> _dictionary.TryAdd(key, value);

				public bool TryGet(object key, out object value)
					=> _dictionary.TryGetValue(key, out value);

				public void Dispose()
				{
					foreach (var pair in _dictionary)
						(pair.Value as IDisposable)?.Dispose();
				}
			}

			sealed class Heap : IHeapOrCache
			{
				static ConcurrentBag<object> ActivateBag()
					=> new ConcurrentBag<object>();

				readonly ConcurrentDictionary<object, object> _dictionary
					= new ConcurrentDictionary<object, object>();
				readonly Func<ConcurrentBag<object>> _activateBag = ActivateBag;

				ConcurrentBag<object> Get(object key)
					=> (ConcurrentBag<object>)_dictionary.GetOrAdd(key, _activateBag);

				public void Set(object key, object value)
					=> Get(key).Add(value);

				public bool TryGet(object key, out object value)
					=> Get(key).TryTake(out value);

				public void Dispose()
				{
					foreach (var pair in _dictionary)
					{
						foreach (var value in ((ConcurrentBag<object>)pair.Value))
							(value as IDisposable)?.Dispose();
					}
				}
			}

			public static HeapOrCache ActivateCache(Scheduler scheduler) => 
				new HeapOrCache(scheduler, new Cache());

			public static HeapOrCache ActivateHeap(Scheduler scheduler) => 
				new HeapOrCache(scheduler, new Heap());

			Scheduler _scheduler;
			IHeapOrCache _heapOrCache;

			HeapOrCache(Scheduler scheduler, IHeapOrCache container)
			{
				_scheduler = scheduler;
				_heapOrCache = container;
			}

			void Log(string format, params object[] arguments)
				=> Profile.WriteLog(format, arguments);

			public object Get<T>(T key = default)
				where T : IAnticipatable
			{
				if (_heapOrCache.TryGet(key, out var value))
				{
					Log("CACHE HIT: {0}", key);
					return value;
				}
				Log("CACHE MISS: {0}", key);
				return key.Get();
			}

			public void Anticipate<T>(T key = default)
				where T : IAnticipatable
			{
				_scheduler.Schedule(() =>
				{
					try
					{
						var stopwatch = new Stopwatch();
						stopwatch.Start();
						_heapOrCache.Set(key, key.Get());
						var ticks = stopwatch.ElapsedTicks;

						Log("CASHED: {0}, ms={1}", key, TimeSpan.FromTicks(ticks).Milliseconds);
					}
					catch (Exception ex)
					{
						Log("EXCEPTION: {0}: {1}", key, ex);
					}
				});
			}

			public void Dispose()
				=> _heapOrCache.Dispose();
		}
#endif
		protected struct ClassConstruction : IAnticipatable
		{
			private Type _type;

			public ClassConstruction(Type type)
			{
				_type = type;
			}

			object IAnticipatable.Get()
			{
				RuntimeHelpers.RunClassConstructor(_type.TypeHandle);
				return null;
			}

			public override string ToString()
			{
				return ".cctor=" + _type.Name;
			}
		}

		public static void AnticipateAllocation<T>(T key = default)
			where T : IAnticipatable
			=> Singleton._heap.Anticipate(key);

		public static object Allocate<T>(T key = default)
			where T : IAnticipatable
			=> Singleton._heap.Get(key);

		public static void AnticipateValue<T>(T key = default)
			where T : IAnticipatable
			=> Singleton._cache.Anticipate(key);

		public static object Get<T>(T key = default)
			where T : IAnticipatable
			=> Singleton._cache.Get(key);

		internal static Anticipator Singleton;

#if NETSTANDARD2_0
		readonly Scheduler _scheduler;
		readonly HeapOrCache _cache;
		readonly HeapOrCache _heap;
#endif
		/// <summary>
		/// Anticipator is composed of a thread, a heap, and a cache. The thread
		/// is used at startup to compute values and allocate resources the UIThread
		/// would otherwise have to compute and allocate.
		/// </summary>
		public Anticipator()
		{
			Singleton = this;

#if NETSTANDARD2_0
			_scheduler = new Scheduler();
			_heap = HeapOrCache.ActivateHeap(_scheduler);
			_cache = HeapOrCache.ActivateCache(_scheduler);
#endif
		}

		protected internal virtual object Activate(Type type, params object[] arguments)
		{
			return Activator.CreateInstance(type, arguments);
		}

		public void Dispose()
		{
			_scheduler.Join();
			_cache.Dispose();
			_heap.Dispose();
		}

#if NETSTANDARD2_0
		/// <summary>
		/// Activates a thread for a constant duration during which time actions
		/// can be scheduled. Calling Join form the UIThread will block until the 
		/// Scheduler thread exits.
		/// </summary>
		private class Scheduler
		{
			private static readonly TimeSpan LoopTimeOut = TimeSpan.FromSeconds(5.0);
			private readonly Thread _thread;
			private readonly AutoResetEvent _work;
			private readonly AutoResetEvent _done;
			private readonly ConcurrentQueue<Action> _actions;

			internal Scheduler()
			{
				_actions = new ConcurrentQueue<Action>();
				_work = new AutoResetEvent(false);
				_done = new AutoResetEvent(false);
				_thread = new Thread(new ParameterizedThreadStart(Loop));
				_thread.Start(_work);
			}

			private void Loop(object argument)
			{
				var autoResetEvent = (AutoResetEvent)argument;

				while (autoResetEvent.WaitOne(LoopTimeOut))
				{
					while (_actions.Count > 0)
					{
						Action action;
						if (_actions.TryDequeue(out action))
							action();
					}
				}

				_done.Set();
			}

			internal void Schedule(Action action)
			{
				if (action == null)
					throw new ArgumentNullException(nameof(action));

				_actions.Enqueue(action);
				_work.Set();
			}

			internal void Join()
				=> _done.WaitOne();
		}
#endif
	}
}

