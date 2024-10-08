﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Linq.Expressions;
using Squared.Util.Bind;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Squared.Util;

namespace Squared.Threading {
    public delegate void OnFutureResolved (IFuture future);
    public delegate void OnFutureResolved<T> (Future<T> future);

    public class FutureException : Exception {
        public FutureException (string message, Exception innerException)
            : base(message, innerException) {
        }
    }

    public class FutureAlreadyHasResultException : InvalidOperationException {
        public readonly IFuture Future;

        public FutureAlreadyHasResultException (IFuture future)
            : base("Future already has a result") {
            Future = future;
        }
    }

    public class FutureHasNoResultException : InvalidOperationException {
        public readonly IFuture Future;

        public FutureHasNoResultException (IFuture future)
            : base("Future does not yet have a result") {
            Future = future;
        }
    }

    public class FutureDisposedException : InvalidOperationException {
        public readonly IFuture Future;

        public FutureDisposedException (IFuture future)
            : base("Future is disposed") {
            Future = future;
        }
    }

    public class FutureHandlerException : Exception {
        public readonly IFuture Future;
        public readonly Delegate Handler;

        public FutureHandlerException (IFuture future, Delegate handler, Exception innerException) 
            : base("One of the Future's handlers threw an uncaught exception", innerException) {
            Future = future;
            Handler = handler;
        }
    }

    internal static class FutureHelpers {
        public static readonly WaitCallback RunInThreadHelper;

        static FutureHelpers () {
            RunInThreadHelper = _RunInThreadHelper;
        }

        internal static void _RunInThreadHelper (object state) {
            var thunk = (RunInThreadThunk)state;
            try {
                thunk.Invoke();
            } catch (System.Reflection.TargetInvocationException ex) {
                thunk.Fail(ex.InnerException);
            } catch (Exception ex) {
                thunk.Fail(ex);
            }
        }
    }

    internal abstract class RunInThreadThunk {
        public abstract void Fail (Exception e);
        public abstract void Invoke ();
    }

    internal class ActionRunInThreadThunk : RunInThreadThunk {
        public readonly SignalFuture Future = new SignalFuture();
        public Action WorkItem;

        public override void Invoke () {
            WorkItem();
            Future.Complete();
        }

        public override void Fail (Exception ex) {
            Future.Fail(ex);
        }
    }

    internal class FuncRunInThreadThunk<T> : RunInThreadThunk {
        public readonly Future<T> Future = new Future<T>();
        public Func<T> WorkItem;

        public override void Invoke () {
            Future.Complete(WorkItem());
        }

        public override void Fail (Exception ex) {
            Future.Fail(ex);
        }
    }

    internal class DynamicRunInThreadThunk : RunInThreadThunk {
        public readonly IFuture Future = new Future<object>();
        public object[] Arguments;
        public Delegate WorkItem;

        public override void Invoke () {
            Future.Complete(WorkItem.DynamicInvoke(Arguments));
        }

        public override void Fail (Exception ex) {
            Future.Fail(ex);
        }
    }

    public interface IFuture : IDisposable {
        bool Failed {
            get;
        }
        bool Completed {
            get;
        }
        bool Disposed {
            get;
        }
        object Result {
            get;
        }
        object Result2 {
            get;
        }
        Exception Error {
            get;
        }
        Type ResultType {
            get;
        }

        bool GetResult (out object result, out Exception error);
        void SetResult (object result, Exception error);
        void SetResult2 (object result, ExceptionDispatchInfo errorInfo);
        void RegisterHandlers (OnFutureResolved completeHandler, OnFutureResolved disposeHandler);
        void RegisterOnResolved (OnFutureResolved handler);
        void RegisterOnComplete (OnFutureResolved handler);
        void RegisterOnDispose (OnFutureResolved handler);
        void RegisterOnErrorCheck (Action handler);
        bool CopyFrom (IFuture source);
    }

    public class NoneType {
        private NoneType () {
        }

        public static readonly NoneType None = new NoneType();
    }

    public class SignalFuture : Future<NoneType> {
        public static readonly SignalFuture Signaled = new SignalFuture(true);

        public SignalFuture ()
            : base() {
        }

        public SignalFuture (bool signaled) 
            : base() {

            if (signaled)
                base.SetResult(NoneType.None, null);
        }
    }

    public static class Future {
        public static Future<T> New<T> () {
            return new Future<T>();
        }

        public static Future<T> New<T> (T value) {
            return new Future<T>(value);
        }

        public static Future<IFuture> WaitForFirst (IEnumerable<IFuture> futures) {
            return WaitForFirst(futures.ToArray());
        }

        public static Future<IFuture> WaitForFirst (params IFuture[] futures) {
            return WaitForX(futures, futures.Length);
        }

        public static IFuture WaitForAll (IEnumerable<IFuture> futures) {
            return WaitForAll(futures.ToArray());
        }

        public static IFuture WaitForAll (params IFuture[] futures) {
            return WaitForX(futures, 1);
        }

        public static Future<U> RunInThread<U> (Func<U> workItem) {
            var thunk = new FuncRunInThreadThunk<U> {
                WorkItem = workItem,
            };
            ThreadPool.QueueUserWorkItem(FutureHelpers.RunInThreadHelper, thunk);
            return thunk.Future;
        }

        public static SignalFuture RunInThread (Action workItem) {
            var thunk = new ActionRunInThreadThunk {
                WorkItem = workItem,
            };
            ThreadPool.QueueUserWorkItem(FutureHelpers.RunInThreadHelper, thunk);
            return thunk.Future;
        }

        public static IFuture RunInThread (Delegate workItem, params object[] arguments) {
            var thunk = new DynamicRunInThreadThunk {
                WorkItem = workItem,
                Arguments = arguments
            };
            ThreadPool.QueueUserWorkItem(FutureHelpers.RunInThreadHelper, thunk);
            return thunk.Future;
        }

        private class WaitHandler {
            public Future<IFuture> Composite;
            public readonly List<IFuture> State = new List<IFuture>();
            public int TriggerAtPreviousCount;
            private bool Disposing = false;

            public void OnCompositeDispose (IFuture f) {
                if (Disposing)
                    return;

                lock (State)
                try {
                    Disposing = true;

                    foreach (var future in State)
                        future.Dispose();

                    State.Clear();
                } finally {
                    Disposing = false;
                }
            }

            public void OnComplete (IFuture f) {
                bool completed = false;
                lock (State) {
                    if (State.Count == TriggerAtPreviousCount) {
                        completed = true;
                        State.Clear();
                    } else {
                        if (!State.Remove(f))
                            throw new ThreadStateException();
                    }
                }

                if (completed)
                    Composite.Complete(f);
            }

            public void OnDispose (IFuture f) {
                bool completed = false;
                lock (State) {
                    if (State.Count == TriggerAtPreviousCount) {
                        completed = true;
                        State.Clear();
                    } else {
                        if (!State.Remove(f))
                            throw new ThreadStateException();
                    }
                }

                if (completed)
                    Composite.Dispose();
            }
        }

        private static Future<IFuture> WaitForX (IEnumerable<IFuture> futures, int triggerAtPreviousCount) {
            if (futures == null)
                throw new ArgumentException("Must specify at least one future to wait on", "futures");

            var f = new Future<IFuture>();
            var h = new WaitHandler();

            h.Composite = f;
            h.State.AddRange(futures);
            h.TriggerAtPreviousCount = triggerAtPreviousCount;

            OnFutureResolved oc = h.OnComplete;
            OnFutureResolved od = h.OnDispose;

            if (h.State.Count == 0)
                throw new ArgumentException("Must specify at least one future to wait on", "futures");

            f.RegisterOnDispose(h.OnCompositeDispose);

            foreach (IFuture _ in futures)
                _.RegisterHandlers(oc, od);

            return f;
        }

        private class WaitForSingleEventThunk {
            public readonly Squared.Util.Bind.BoundMember<EventHandler> Event;
            public readonly SignalFuture Future = new SignalFuture();
            private readonly EventHandler Handler;

            public WaitForSingleEventThunk (Squared.Util.Bind.BoundMember<EventHandler> evt) {
                Event = evt;
                Handler = OnEvent;

                Event.Add(Handler);
                Future.RegisterOnDispose((Threading.OnFutureResolved)this.OnDispose);
            }

            public void OnEvent (object sender, EventArgs args) {
                OnDispose(Future);
                Future.Complete();
            }

            public void OnDispose (IFuture future) {
                Event.Remove(Handler);
            }
        }

        private class WaitForSingleEventThunk<TEventArgs>
            where TEventArgs : System.EventArgs {

            public readonly Squared.Util.Bind.BoundMember<EventHandler<TEventArgs>> Event;
            public readonly Future<TEventArgs> Future = new Future<TEventArgs>();
            private readonly EventHandler<TEventArgs> Handler;

            public WaitForSingleEventThunk (Squared.Util.Bind.BoundMember<EventHandler<TEventArgs>> evt) {
                Event = evt;
                Handler = OnEvent;

                Event.Add(Handler);
                Future.RegisterOnDispose((Threading.OnFutureResolved)this.OnDispose);
            }

            public void OnEvent (object sender, TEventArgs args) {
                OnDispose(Future);
                Future.SetResult(args, null);
            }

            public void OnDispose (IFuture future) {
                Event.Remove(Handler);
            }
        }

        public static Future<TEventArgs> WaitForSingleEvent<TEventArgs>
            (Squared.Util.Bind.BoundMember<EventHandler<TEventArgs>> evt)
            where TEventArgs : System.EventArgs 
        {
            return (new WaitForSingleEventThunk<TEventArgs>(evt)).Future;
        }

        public static SignalFuture WaitForSingleEvent(Squared.Util.Bind.BoundMember<EventHandler> evt) {
            return (new WaitForSingleEventThunk(evt)).Future;
        }

        public static Future<TEventArgs> WaitForSingleEvent<TEventArgs>
            (Expression<Action<EventHandler<TEventArgs>>> evt)
            where TEventArgs : System.EventArgs {

            var bm = Squared.Util.Bind.BoundMember.New(evt);
            return WaitForSingleEvent(bm);
        }

        public static SignalFuture WaitForSingleEvent (Expression<Action<EventHandler>> evt) {
            var bm = Squared.Util.Bind.BoundMember.New(evt);
            return WaitForSingleEvent(bm);
        }
    }

    public class Future<T> : IDisposable, IFuture {
        private static readonly int ProcessorCount;

        private const int State_Empty = 0;
        private const int State_Indeterminate = 1;
        private const int State_CompletedWithValue = 2;
        private const int State_CompletedWithError = 3;
        private const int State_Disposed = 4;
        private const int State_Disposing = 5;

        private volatile int _State = State_Empty;
        private DenseList<Delegate> _OnCompletes, _OnDisposes;
        private ExceptionDispatchInfo _ErrorInfo = null;
        private Exception _Error = null;
        private Action _OnErrorChecked = null;
        private T _Result = default(T);

        public override string ToString () {
            int state = _State;
            var result = _Result;
            var error = (_ErrorInfo != null) ? _ErrorInfo.SourceException : _Error;
            string stateText = "??";
            switch (state) {
                case State_Empty:
                    stateText = "Empty";
                    break;
                case State_Indeterminate:
                    stateText = "Indeterminate";
                    break;
                case State_CompletedWithValue:
                    stateText = "CompletedWithValue";
                    break;
                case State_CompletedWithError:
                    stateText = "CompletedWithError";
                    break;
                case State_Disposed:
                case State_Disposing:
                    stateText = "Disposed";
                    break;
            }
            return String.Format("<Future<{3}> ({0}) r={1} e={2}>", stateText, result, error, typeof(T).Name);
        }

        /// <summary>
        /// Creates a future that is not yet completed
        /// </summary>
        public Future () {
        }

        /// <summary>
        /// Creates a future that is already completed with a value
        /// </summary>
        public Future (T value) {
            this.SetResult(value, null);
        }

        static Future () {
            ProcessorCount = Environment.ProcessorCount;
        }

        private static void SpinWait (int iterationCount) {
            if ((iterationCount < 5) && (ProcessorCount > 1)) {
                Thread.SpinWait(7 * iterationCount);
            } else if (iterationCount < 7) {
                Thread.Sleep(0);
            } else {
                Thread.Sleep(1);
            }
        }

        private void InvokeHandler (Delegate handler) {
            OnFutureResolved untyped;
            OnFutureResolved<T> typed;
            try {
                if ((typed = handler as OnFutureResolved<T>) != null) {
                    typed(this);
                } else if ((untyped = handler as OnFutureResolved) != null) {
                    untyped(this);
                } else {
                    throw new Exception("Invalid future handler");
                }
            } catch (Exception exc) {
                throw new FutureHandlerException(this, handler, exc);
            }
        }

        private void InvokeHandlers (ref DenseList<Delegate> handlers, ref DenseList<Delegate> otherListToClear) {
            if (handlers.Count <= 0)
                return;

            try {
                otherListToClear.Clear();
                for (int i = 0; i < handlers.Count; i++)
                    InvokeHandler(handlers[i]);
            } finally {
                handlers.Clear();
            }
        }

        void OnErrorCheck () {
            var ec = _OnErrorChecked;
            if (ec != null) {
                ec = Interlocked.Exchange(ref _OnErrorChecked, null);
                if (ec != null)
                    ec();
            }
        }

        /// <summary>
        /// Registers a handler that will be invoked when the error status of this future is checked.
        /// You can use this to identify whether a future has been abandoned.
        /// </summary>
        public void RegisterOnErrorCheck (Action handler) {
            Action newHandler;
            int iterations = 1;

            while (true) {
                var oldHandler = _OnErrorChecked;
                if (oldHandler != null) {
                    newHandler = () => {
                        oldHandler();
                        handler();
                    };
                } else {
                    newHandler = handler;
                }

                if (Interlocked.CompareExchange(ref _OnErrorChecked, newHandler, oldHandler) == oldHandler)
                    break;

                SpinWait(iterations);
                iterations += 1;
            }
        }

        private void ClearIndeterminate (int newState) {
            if (Interlocked.CompareExchange(ref _State, newState, State_Indeterminate) != State_Indeterminate)
                throw new ThreadStateException();
        }

        private int TrySetIndeterminate () {
            int state;
            int iterations = 1;
            while (true) {
                state = Interlocked.CompareExchange(ref _State, State_Indeterminate, State_Empty);

                if (state != State_Indeterminate)
                    break;

                SpinWait(iterations++);
            }
            return state;
        }

        /// <returns>Whether the future is already complete and handlers were run</returns>
        private bool RegisterHandler_Impl (Delegate handler, ref DenseList<Delegate> list, bool forDispose) {
            if (handler == null)
                return false;

            int oldState = TrySetIndeterminate();
                
            if (oldState == State_Empty) {
                list.Add(handler);

                ClearIndeterminate(State_Empty);
                return false;
            } else if (
                (oldState == State_CompletedWithValue) ||
                (oldState == State_CompletedWithError) ||
                (oldState == State_Disposed) ||
                (oldState == State_Disposing)
            ) {
                if (forDispose) {
                    if ((oldState == State_Disposed) || (oldState == State_Disposing)) {
                        InvokeHandler(handler);
                        return true;
                    }
                } else {
                    if ((oldState == State_CompletedWithValue) || (oldState == State_CompletedWithError)) {
                        InvokeHandler(handler);
                        return true;
                    }
                }
                return false;
            } else {
                throw new ThreadStateException();
            }
        }

        /// <summary>
        /// Registers a pair of handlers to be notified upon future completion and disposal, respectively.
        /// </summary>
        public void RegisterHandlers (OnFutureResolved<T> onComplete, OnFutureResolved onDispose) {
            // FIXME: Set state to indeterminate once instead of twice
            if (!RegisterHandler_Impl(onComplete, ref _OnCompletes, false))
                RegisterHandler_Impl(onDispose, ref _OnDisposes, true);
        }

        /// <summary>
        /// Registers a pair of handlers to be notified upon future completion and disposal, respectively.
        /// </summary>
        void IFuture.RegisterHandlers (OnFutureResolved onComplete, OnFutureResolved onDispose) {
            // FIXME: Set state to indeterminate once instead of twice
            if (!RegisterHandler_Impl(onComplete, ref _OnCompletes, false))
                RegisterHandler_Impl(onDispose, ref _OnDisposes, true);
        }

        void IFuture.RegisterOnResolved (OnFutureResolved handler) {
            if (!RegisterHandler_Impl(handler, ref _OnCompletes, false))
                RegisterHandler_Impl(handler, ref _OnDisposes, true);
        }

        /// <summary>
        /// Registers a handler to be notified upon future completion or disposal.
        /// </summary>
        public void RegisterOnResolved (OnFutureResolved<T> handler) {
            if (!RegisterHandler_Impl(handler, ref _OnCompletes, false))
                RegisterHandler_Impl(handler, ref _OnDisposes, true);
        }

        /// <summary>
        /// Registers a handler to be notified upon future completion. If the future is disposed, this will not run.
        /// </summary>
        public void RegisterOnComplete (OnFutureResolved handler) {
            RegisterHandler_Impl(handler, ref _OnCompletes, false);
        }

        /// <summary>
        /// Registers a handler to be notified upon future completion. If the future is disposed, this will not run.
        /// </summary>
        public void RegisterOnComplete2 (OnFutureResolved<T> handler) {
            RegisterHandler_Impl(handler, ref _OnCompletes, false);
        }

        /// <summary>
        /// Registers a handlers to be notified upon future disposal. If the future is completed, this will not run.
        /// </summary>
        public void RegisterOnDispose (OnFutureResolved handler) {
            RegisterHandler_Impl(handler, ref _OnDisposes, true);
        }
        
        /// <summary>
        /// Returns true if the future has been disposed or is in the process of being disposed.
        /// </summary>
        public bool Disposed {
            get {
                return (_State == State_Disposed) || (_State == State_Disposing);
            }
        }

        /// <summary>
        /// Returns true if the future has been completed.
        /// Note that if the future is currently being completed, this may return false.
        /// </summary>
        public bool Completed {
            get {
                int state = _State;
                return (state == State_CompletedWithValue) || (state == State_CompletedWithError);
            }
        }

        /// <summary>
        /// Returns true if the future has been completed but was completed with an error.
        /// Note that if the future is currently being completed, this may return false.
        /// </summary>
        public bool Failed {
            get {
                OnErrorCheck();
                return _State == State_CompletedWithError;
            }
        }

        Type IFuture.ResultType {
            get {
                return typeof(T);
            }
        }

        object IFuture.Result {
            get {
                return this.Result;
            }
        }

        object IFuture.Result2 {
            get {
                return this.Result2;
            }
        }

        /// <summary>
        /// Returns the exception that caused the future to fail, if it has failed.
        /// </summary>
        public Exception Error {
            get {
                int state = _State;
                if (state == State_CompletedWithValue) {
                    return null;
                } else if (state == State_CompletedWithError) {
                    OnErrorCheck();
                    return (_ErrorInfo != null) ? _ErrorInfo.SourceException : _Error;
                } else if ((state == State_Disposed) || (state == State_Disposing)) {
                    return null;
                } else
                    throw new FutureHasNoResultException(this);
            }
        }

        /// <summary>
        /// Returns the future's result if it contains one. 
        /// If it failed, a FutureException will be thrown, with the original exception as its InnerException.
        /// </summary>
        public T Result {
            get {
                int state = _State;
                if (state == State_CompletedWithValue) {
                    return _Result;
                } else if (state == State_CompletedWithError) {
                    OnErrorCheck();
                    throw new FutureException("Future's result was an error", (_ErrorInfo != null) ? _ErrorInfo.SourceException : _Error);
                } else if ((state == State_Disposed) || (state == State_Disposing)) {
                    throw new FutureDisposedException(this);
                } else
                    throw new FutureHasNoResultException(this);
            }
        }

        /// <summary>
        /// Returns the future's result if it contains one.
        /// If it failed, the exception responsible will be rethrown with its stack preserved (if possible).
        /// </summary>
        public T Result2 {
            get {
                int state = _State;
                if (state == State_CompletedWithValue) {
                    return _Result;
                } else if (state == State_CompletedWithError) {
                    OnErrorCheck();
                    if (_ErrorInfo != null) {
                        _ErrorInfo.Throw();
                        throw new InvalidOperationException();
                    } else
                        throw new FutureException("Future's result was an error", _Error);
                } else if ((state == State_Disposed) || (state == State_Disposing)) {
                    throw new FutureDisposedException(this);
                } else
                    throw new FutureHasNoResultException(this);
            }
        }

        bool IFuture.CopyFrom (IFuture source) {
            var tSelf = source as Future<T>;
            if (tSelf != null)
                return CopyFrom(tSelf);
            else {
                object r;
                Exception e;
                if (source.GetResult(out r, out e)) {
                    if (r == null)
                        SetResult(default(T), e);
                    else
                        SetResult((T)r, e);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Attempts to the current state of the source future to this future.
        /// May fail for various reasons (for example, this future is already completed).
        /// </summary>
        /// <returns>true if the copy was successful.</returns>
        public bool CopyFrom (Future<T> source) {
            var state = source._State;

            // FIXME: Wait if it's indeterminate state
            if ((state == State_Indeterminate) || (state == State_Empty))
                return false;

            if (!SetResultPrologue())
                return false;

            _Result = source._Result;
            _Error = source._Error;
            _ErrorInfo = source._ErrorInfo;

            SetResultEpilogue(state);
            return true;
        }

        void IFuture.SetResult (object result, Exception error) {
            if ((error != null) && (result != null)) {
                throw new FutureException("Cannot complete a future with both a result and an error.", error);
            }

            if (result == null)
                SetResult(default(T), error);
            else {
                T value;
                try {
                    value = (T)result;
                } catch (Exception exc) {
                    value = default(T);
                    error = new FutureException("Could not convert result to correct type", exc);
                }
                SetResult(value, error);
            }
        }

        void IFuture.SetResult2 (object result, ExceptionDispatchInfo errorInfo) {
            if ((errorInfo != null) && (result != null)) {
                throw new FutureException("Cannot complete a future with both a result and an error.", errorInfo.SourceException);
            }

            if (result == null)
                SetResult2(default(T), errorInfo);
            else
                SetResult2((T)result, errorInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool SetResultPrologue (bool throwOnFailure = true) {
            int iterations = 1;

            while (true) {
                int oldState = Interlocked.CompareExchange(ref _State, State_Indeterminate, State_Empty);

                if ((oldState == State_Disposed) || (oldState == State_Disposing)) {
                    return false;
                } else if ((oldState == State_CompletedWithValue) || (oldState == State_CompletedWithError)) {
                    if (throwOnFailure)
                        throw new FutureAlreadyHasResultException(this);
                    else
                        return false;
                } else if (oldState == State_Empty)
                    break;

                SpinWait(iterations++);
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetResultEpilogue (int newState) {
            if (Interlocked.Exchange(ref _State, newState) != State_Indeterminate)
                throw new ThreadStateException();

            if ((newState == State_CompletedWithValue) || (newState == State_CompletedWithError))
                InvokeHandlers(ref _OnCompletes, ref _OnDisposes);
        }

        /// <summary>
        /// Completes the future. If the provided task failed, its exception will be stored into this future.
        /// </summary>
        public void SetResultFrom (System.Threading.Tasks.Task task) {
            if (!task.IsCompleted)
                throw new InvalidOperationException("Task not completed");

            if (!SetResultPrologue())
                return;

            int newState = task.IsFaulted ? State_CompletedWithError : State_CompletedWithValue;
            _Error = null;
            if (newState == State_CompletedWithError) {
                _ErrorInfo = ExceptionDispatchInfo.Capture(task.Exception.InnerExceptions.Count == 1 ? task.Exception.InnerException : task.Exception);
            } else {
                _ErrorInfo = null;
            }

            SetResultEpilogue(newState);
        }

        /// <summary>
        /// Sets the result of this future to the result of a completed task.
        /// If the task failed, the exception will be stored into this future.
        /// </summary>
        public void SetResultFrom (System.Threading.Tasks.Task<T> task) {
            if (!task.IsCompleted)
                throw new InvalidOperationException("Task not completed");

            if (!SetResultPrologue())
                return;

            int newState = task.IsFaulted ? State_CompletedWithError : State_CompletedWithValue;
            _Error = null;
            if (newState == State_CompletedWithError) {
                // FIXME: Assign result here?
                _ErrorInfo = ExceptionDispatchInfo.Capture(task.Exception.InnerExceptions.Count == 1 ? task.Exception.InnerException : task.Exception);
            } else {
                _Result = task.Result;
                _ErrorInfo = null;
            }

            SetResultEpilogue(newState);
        }

        /// <summary>
        /// Sets the result of this future along with information on the responsible exception (if any).
        /// This information will be used to rethrow with stack preserved, as necessary.
        /// </summary>
        public void SetResult2 (T result, ExceptionDispatchInfo errorInfo) {
            if (!SetResultPrologue())
                return;

            int newState = (errorInfo != null) ? State_CompletedWithError : State_CompletedWithValue;
            _Result = result;
            _Error = null;
            _ErrorInfo = errorInfo;

            SetResultEpilogue(newState);
        }

        /// <summary>
        /// Sets the result of this future along with the responsible exception (if any).
        /// The exception will be wrapped instead of being rethrown.
        /// </summary>
        public void SetResult (T result, Exception error) {
            if (!SetResultPrologue())
                return;

            int newState = (error != null) ? State_CompletedWithError : State_CompletedWithValue;
            _Result = result;
            _Error = error;
            _ErrorInfo = null;

            SetResultEpilogue(newState);
        }

        /// <summary>
        /// Sets the result of this future along with the responsible exception (if any).
        /// The exception will be wrapped instead of being rethrown.
        /// If the future already has a result or has been disposed, this method will return false.
        /// </summary>
        public bool TrySetResult (T result, Exception error) {
            if (!SetResultPrologue())
                return false;

            int newState = (error != null) ? State_CompletedWithError : State_CompletedWithValue;
            _Result = result;
            _Error = error;
            _ErrorInfo = null;

            SetResultEpilogue(newState);
            return true;
        }

        /// <summary>
        /// Disposes the future and invokes any OnDispose handlers.
        /// No OnComplete handlers will be invoked.
        /// </summary>
        public void Dispose () {
            int iterations = 1;

            while (true) {
                int oldState = Interlocked.CompareExchange(ref _State, State_Disposing, State_Empty);

                if ((oldState == State_Disposed) || 
                    (oldState == State_Disposing) ||
                    (oldState == State_CompletedWithValue) || 
                    (oldState == State_CompletedWithError)
                ) {
                    return;
                } else if (oldState == State_Empty)
                    break;

                SpinWait(iterations++);
            }

            // FIXME: Possible leak / failure to invoke, but probably fine?
            InvokeHandlers(ref _OnDisposes, ref _OnCompletes);

            if (Interlocked.Exchange(ref _State, State_Disposed) != State_Disposing)
                throw new ThreadStateException();
        }

        bool IFuture.GetResult (out object result, out Exception error) {
            T temp;
            bool retval = this.GetResult(out temp, out error);

            if (error != null)
                result = null;
            else
                result = temp;

            return retval;
        }

        /// <summary>
        /// Attempts to retrieve the future's result.
        /// Will return false if the future completed with an error, was disposed, or is not completed.
        /// </summary>
        public bool GetResult (out T result) {
            if (_State == State_CompletedWithValue) {
                Thread.MemoryBarrier();
                if (_State == State_CompletedWithValue) {
                    result = _Result;
                    return true;
                }
            }

            result = default(T);
            return false;
        }

        /// <summary>
        /// Attempts to retrieve the future's result.
        /// Will return false if the future was disposed or is not completed.
        /// </summary>
        public bool GetResult (out T result, out Exception error) {
            int state = _State;

            if (state == State_CompletedWithValue) {
                Thread.MemoryBarrier();
                if (_State == State_CompletedWithValue) {
                    result = _Result;
                    error = null;
                    return true;
                }
            }
            
            if (state == State_CompletedWithError) {
                Thread.MemoryBarrier();
                if (_State == State_CompletedWithError) {
                    OnErrorCheck();
                    result = default(T);
                    error = (_ErrorInfo != null) ? _ErrorInfo.SourceException : _Error;
                    return true;
                }
            }

            result = default(T);
            error = null;
            return false;
        }
    }
    
    public static class FutureExtensionMethods {
        /// <summary>
        /// Causes this future to become completed when the specified future is completed.
        /// </summary>
        public static void Bind (this IFuture future, IFuture target) {
            OnFutureResolved handler = (f) => {
                future.CopyFrom(f);
            };
            target.RegisterOnComplete(handler);
        }

        /// <summary>
        /// Causes this future to become completed when the specified future is completed.
        /// </summary>
        public static IFuture Bind<T> (this IFuture future, Expression<Func<T>> target) {
            var member = BoundMember.New(target);

            future.RegisterOnComplete((_) => {
                Exception error;
                object result;
                if (future.GetResult(out result, out error))
                    ((IBoundMember)member).Value = result;
            });

            return future;
        }

        /// <summary>
        /// Causes this future to become completed when the specified future is completed.
        /// </summary>
        public static Future<T> Bind<T> (this Future<T> future, Expression<Func<T>> target) {
            var member = BoundMember.New(target);

            future.RegisterOnComplete2((f) => {
                T result;
                if (future.GetResult(out result))
                    member.Value = result;
            });
            
            return future;
        }

        public static void AssertSucceeded (this IFuture future) {
            if (future.Failed)
                throw new FutureException("Operation was expected to succeed", future.Error);
        }

        public static void Complete (this IFuture future) {
            future.SetResult(null, null);
        }

        public static void Complete (this IFuture future, object result) {
            future.SetResult(result, null);
        }

        public static void Complete<T> (this Future<T> future, T result) {
            future.SetResult(result, null);
        }

        public static void Fail (this IFuture future, Exception error) {
            // future.SetResult(null, error);
            future.SetResult2(null, ExceptionDispatchInfo.Capture(error));
        }

        public static void Fail<T> (this Future<T> future, Exception error) {
            future.SetResult2(default(T), ExceptionDispatchInfo.Capture(error));
        }

        public static bool CheckForFailure (this IFuture future, params Type[] failureTypes) {
            object result;
            Exception error;
            if (future.GetResult(out result, out error)) {
                if (error != null) {
                    foreach (Type type in failureTypes)
                        if (type.IsInstanceOfType(error))
                            return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Creates a ManualResetEventSlim that will become set when this future is completed.
        /// </summary>
        public static ManualResetEventSlim GetCompletionEvent (this IFuture future) {
            ManualResetEventSlim evt = new ManualResetEventSlim(false);
            OnFutureResolved handler = (f) => evt.Set();
            future.RegisterOnComplete(handler);
            return evt;
        }
    }
}
