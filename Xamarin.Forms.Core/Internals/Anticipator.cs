// Decompiled with JetBrains decompiler
// Type: Xamarin.Forms.Core.Internals.Anticipator
// Assembly: Xamarin.Forms.Core, Version=2.0.0.0, Culture=neutral, PublicKeyToken=798c2ba514f889d3
// MVID: 8D0BDDA4-D80F-455B-A9D6-94A77E6BBCA2
// Assembly location: C:\Users\Chris\Desktop\Xamarin.Forms.Core.dll

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
	public abstract class Anticipator
	{
		internal static Anticipator Singleton;

		public interface IAnticipatable
		{
			object Get();
		}

		public interface IStore : IDisposable
		{
			void Set(object key, object value);
			object Get(object key);
		}

#if NETSTANDARD2_0
		public class Bag : ConcurrentBag<object> : IStore
		{
			void IDisposable.Dispose()

			object IStore.Get(object key)
				=> this.TryTake()

			void IStore.Set(object key, object value)
		}
#endif

#if NETSTANDARD2_0
		abstract class AnticipatedValues : IDisposable
		{
			ThreadPool _threadPool;
			IEnumerable _values;

			public AnticipatedValues(ThreadPool threadPool, IEnumerable values)
			{
				_threadPool = threadPool;
				_values = values;
			}

			protected abstract void Put(object key, object value);
			protected abstract object Pull(object key);

			protected void Log(string format, params object[] arguments)
				=> Profile.WriteLog(format, arguments);

			public void Anticipate<T>(T key = default)
				where T : IAnticipatable
			{
				_threadPool.Schedule(() =>
				{
					try
					{
						var stopwatch = new Stopwatch();
						stopwatch.Start();
						Put(key, key.Get());
						Log("CASHED: {0}, ms={1}", key, TimeSpan.FromTicks(stopwatch.ElapsedTicks).Milliseconds);
					}
					catch (Exception ex)
					{
						Log("EXCEPTION: {0}: {1}", key, ex);
					}
				});
			}

			public object Get()
			{
				if (_cache.TryGetValue(key, out var value))
				{
					Log("CACHE HIT: {0}", key);
					return value;
				}
				Log("CACHE MISS: {0}", key);
				return key.Get();

			}

			public virtual void Dispose()
			{
				foreach (var disposable in _values)
					(disposable as IDisposable)?.Dispose();
			}
		}
#endif

#if NETSTANDARD2_0
		class Cache : AnticipatedValues
		{
			ConcurrentDictionary<object, object> _cache;

			public Cache(ThreadPool threadPool)
				: this(threadPool, new ConcurrentDictionary<object, object>())
			{ }

			Cache(ThreadPool threadPool, ConcurrentDictionary<object, object> cache)
				: base(threadPool, cache)
			{
				_cache = cache;
			}

			protected override void Put(object key, object value)
				=> _cache.TryAdd(key, value);

			protected object Get<T>(T key = default)
				where T : IAnticipatable
			{
			}
		}
#endif

#if NETSTANDARD2_0
		class Heap : AnticipatedValues
		{
			struct Slot
			{
				object Key;
				object Value;

				public Slot(object key, object value)
				{
					Key = key;
					Value = value;
				}

				public override bool Equals(object key)
					=> Equals(Key, key);
				public override int GetHashCode()
					=> Key.GetHashCode();
			}

			ConcurrentBag<object> _heap;

			public Heap(ThreadPool threadPool)
				: this(threadPool, new ConcurrentBag<object>())
			{ }

			Heap(ThreadPool threadPool, ConcurrentBag<object> heap)
				: base(threadPool, heap)
			{
				_heap = heap;
			}

			protected override void Put(object key, object value)
				=> _heap.Add(new Slot(key, value));
		}
#endif

#if NETSTANDARD2_0
		ThreadPool _threadPool;
		Cache _cache;
		Heap _heap;
#endif

		public Anticipator()
		{
			Singleton = this;

#if NETSTANDARD2_0
			_threadPool = new ThreadPool(() => {
				_cache.Dispose();
				_heap.Dispose();
			});

			_heap = new Heap(_threadPool);
			_cache = new Cache(_threadPool);
#endif
		}

		protected internal virtual object Activate(Type type, params object[] arguments)
		{
			return Activator.CreateInstance(type, arguments);
		}

		public void AnticipateAllocation<T>(T key = default)
			where T : IAnticipatable
		{
		}

		public void Allocate<T>(T key = default)
			where T : IAnticipatable
		{
		}

		public void AnticipateValue<T>(T key = default) 
			where T : IAnticipatable
		{
		}

		public void Get<T>()
			where T : IAnticipatable
		{
		}

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

#if NETSTANDARD2_0
		private class ThreadPool
		{
			private static readonly TimeSpan LoopTimeOut = TimeSpan.FromSeconds(5.0);
			private static readonly int Concurrency = 1;
			private int _threadCount = Concurrency;
			private readonly Thread[] _threads;
			private readonly AutoResetEvent[] _signals;
			private readonly ConcurrentQueue<Action> _actions;
			private readonly Action _cleanup;

			internal ThreadPool(Action cleanup)
			{
				_cleanup = cleanup;
				_actions = new ConcurrentQueue<Action>();
				_threads = new Thread[Concurrency];
				_signals = new AutoResetEvent[Concurrency];
				_signals[0] = new AutoResetEvent(true);
				_threads[0] = new Thread(new ParameterizedThreadStart(Loop));
				_threads[0].Start(_signals[0]);

				if (Concurrency <= 1)
					return;

				Schedule(() =>
				{
					for (int i = 1; i < _threads.Length; ++i)
					{
						_signals[i] = new AutoResetEvent(true);
						_threads[i] = new Thread(new ParameterizedThreadStart(Loop));
						_threads[i].Start(_signals[i]);
					}
				});
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
				Interlocked.Decrement(ref _threadCount);

				if (_threadCount != 0)
					return;

				_cleanup();
			}

			private void Signal()
			{
				for (int i = 0; i < _signals.Length; ++i)
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
#endif
	}
}

