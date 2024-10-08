﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Squared.Util;

namespace Squared.Threading {
    public class GroupThread : IDisposable {
        public  readonly ThreadGroup          Owner;
        public  readonly Thread               Thread;
        private readonly ManualResetEventSlim WakeSignal = new ManualResetEventSlim(true);

#if DEBUG
        // HACK: Set the delay to EXTREMELY LONG so that we will get nasty pauses
        //  in debug builds if we hit a latent bug, so that it will be obvious
        //  they are there and we can debug them
        public static int IdleWaitDurationMs = 10000;
#else
        // HACK: Set a long enough time to avoid churning the thread scheduler
        //  (this will improve efficiency on battery-powered devices, etc)
        // But not TOO long in order to avoid completely collapsing in the event
        //  that a latent bug in our signaling manifests itself
        public static int IdleWaitDurationMs = 500;
#endif
        private object WakeCountLock = new object();
        private int NextQueueIndex;
        private int WakeCount, LastWakeCount;

        public bool IsDisposed { get; private set; }

        internal GroupThread (ThreadGroup owner, int nextQueueIndex) {
            Owner = owner;
            NextQueueIndex = nextQueueIndex;
            Thread = new Thread(ThreadMain);
            Thread.Name = string.Format("ThreadGroup {0} {1} worker #{2}", owner.GetHashCode(), owner.Name, owner.Count);
            Thread.IsBackground = owner.CreateBackgroundThreads;
            if (owner.COMThreadingModel != ApartmentState.Unknown)
                Thread.SetApartmentState(owner.COMThreadingModel);
            // HACK: Ensure the thread-local list has been allocated before we return
            //  since our caller has the threads lock held.
            var queueList = owner.QueueLists.Value;
            Thread.Start(this);
        }

        public void Wake () {
            lock (WakeCountLock)
                WakeCount++;
            WakeSignal.Set();
        }

        private static void AutoReset (WeakReference<GroupThread> weakSelf) {
            if (!weakSelf.TryGetTarget(out GroupThread self))
                return;

            // HACK: We only want to clear our wake signal's state if we're sure we haven't
            //  received another wake request since we were last woken up.
            lock (self.WakeCountLock) {
                if (self.WakeCount <= self.LastWakeCount)
                    self.WakeSignal.Reset();
                self.LastWakeCount = self.WakeCount;
            }
        }

        private static void ThreadMain (object _self) {
            var weakSelf = ThreadMainSetup(ref _self, out UnorderedList<IWorkQueue> queueList, out ManualResetEventSlim wakeSignal);

            // On thread termination we release our event.
            // If we did this in Dispose there'd be no clean way to deal with this.
            while (true) {
                bool moreWorkRemains;
                // HACK: We retain a strong reference to our GroupThread while we're running,
                //  and if our owner GroupThread has been collected, we abort
                if (!ThreadMainStep(weakSelf, queueList, out moreWorkRemains))
                    break;
                // The strong reference is released here so we can wait to be woken up

                // We only wait if no work remains
                if (!moreWorkRemains) {
                    wakeSignal.Wait(IdleWaitDurationMs);
                    AutoReset(weakSelf);
                } else
                    Thread.Yield();
            }
        }

        private static WeakReference<GroupThread> ThreadMainSetup (
            ref object _self, out UnorderedList<IWorkQueue> queueList, out ManualResetEventSlim wakeSignal
        ) {
            var self = (GroupThread)_self;
            var weakSelf = new WeakReference<GroupThread>(self);
            wakeSignal = self.WakeSignal;
            queueList = self.Owner.QueueLists.Value;
            return weakSelf;
        }

        private static bool ThreadMainStep (WeakReference<GroupThread> weakSelf, UnorderedList<IWorkQueue> queueList, out bool moreWorkRemains) {
            // We hold the strong reference at method scope so we can be sure it doesn't get held too long
            GroupThread strongSelf = null;
            moreWorkRemains = false;

            // If our owner object has been collected, abort
            if (!weakSelf.TryGetTarget(out strongSelf))
                return false;

            // If our owner object has been disposed, abort
            if (strongSelf.IsDisposed)
                return false;

            int queueCount;
            IWorkQueue[] queues;
            lock (queueList) {
                queueCount = queueList.Count;
                // Since we only ever add new queues, the worst outcome of a race here
                //  is that we miss the new queue(s) for this step
                queues = queueList.GetBufferArray();
            }

            strongSelf.Owner.ThreadBeganWorking();

            var nqi = strongSelf.NextQueueIndex++;
            for (int i = 0; i < queueCount; i++) {
                if (strongSelf.IsDisposed)
                    return false;

                // We round-robin select a queue from our pool every tick and then step it
                IWorkQueue queue = null;
                int queueIndex = (i + nqi) % queueCount;
                queue = queues[queueIndex];

                if (queue != null) {
                    bool exhausted;
                    int processedItemCount = queue.Step(out exhausted);

                    // HACK: If we processed at least one item in this queue, but more items remain,
                    //  make sure the caller knows not to go to sleep.
                    if (processedItemCount > 0)
                        moreWorkRemains |= !exhausted;
                }
            }

            strongSelf.Owner.ThreadBecameIdle();
            GC.KeepAlive(strongSelf);
            return true;
        }

        public void Dispose () {
            IsDisposed = true;
            Wake();
        }
    }
}
