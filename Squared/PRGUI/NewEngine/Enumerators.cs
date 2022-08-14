﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Squared.PRGUI.Layout;

namespace Squared.PRGUI.NewEngine {
    public partial class LayoutEngine {
        public unsafe struct SiblingEnumerator : IEnumerator<ControlKey> {
            public readonly LayoutEngine Engine;
            public readonly ControlKey FirstItem;
            public readonly ControlKey? LastItem;
            private bool Started, Reverse;
            private int Version;

            public SiblingEnumerator (LayoutEngine engine, ControlKey firstItem, ControlKey? lastItem, bool reverse = false) {
                Engine = engine;
                Version = engine.Version;
                FirstItem = firstItem;
                LastItem = lastItem;
                Started = false;
                _Current = ControlKey.Invalid;
                Reverse = reverse;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void CheckVersion () {
            }

            private ControlKey _Current;
            public ControlKey Current {
                [TargetedPatchingOptOut("")]
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _Current;
            }
            object IEnumerator.Current => _Current;

            public void Dispose () {
                _Current = ControlKey.Invalid;
                Version = -1;
            }

            public bool MoveNext () {
                if (Version != Engine.Version) {
                    Engine.AssertionFailed("Context was modified");
                    return false;
                }

                if (Current.IsInvalid) {
                    if (Started)
                        return false;

                    Started = true;
                    _Current = FirstItem;
                } else {
                    ref var pCurrent = ref Engine[Current];
                    var nextItem = Reverse ? pCurrent.PreviousSibling : pCurrent.NextSibling;
                    if (Current == LastItem)
                        _Current = ControlKey.Invalid;
                    else if (nextItem.IsInvalid)
                        _Current = ControlKey.Invalid;
                    else
                        _Current = nextItem;
                }

                return !Current.IsInvalid;
            }

            void IEnumerator.Reset () {
                if (Version != Engine.Version) {
                    Engine.AssertionFailed("Context was modified");
                    throw new Exception("Context was modified");
                }
                _Current = ControlKey.Invalid;
            }
        }

        public struct SiblingsEnumerable : IEnumerable<ControlKey> {
            public readonly LayoutEngine Engine;
            public readonly ControlKey FirstItem;
            public readonly ControlKey? LastItem;

            internal SiblingsEnumerable (LayoutEngine engine, ControlKey firstItem, ControlKey? lastItem) {
                Engine = engine;
                FirstItem = firstItem;
                LastItem = lastItem;
            }

            public SiblingEnumerator GetEnumerator () {
                return new SiblingEnumerator(Engine, FirstItem, LastItem);
            }

            IEnumerator<ControlKey> IEnumerable<ControlKey>.GetEnumerator () {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator () {
                return GetEnumerator();
            }
        }

        public struct ChildrenEnumerable : IEnumerable<ControlKey> {
            public readonly LayoutEngine Engine;
            public readonly ControlKey Parent;
            public readonly bool Reverse;

            internal ChildrenEnumerable (LayoutEngine engine, ControlKey parent, bool reverse) {
                Engine = engine;
                Parent = parent;
                Reverse = reverse;
            }

            public SiblingEnumerator GetEnumerator () {
                ref var rec = ref Engine[Parent];
                return new SiblingEnumerator(
                    Engine, Reverse ? rec.LastChild : rec.FirstChild, 
                    Reverse ? rec.FirstChild : rec.LastChild, Reverse
                );
            }

            IEnumerator<ControlKey> IEnumerable<ControlKey>.GetEnumerator () {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator () {
                return GetEnumerator();
            }
        }

        public unsafe struct RunEnumerator : IEnumerator<int> {
            private const int State_Disposed = -4,
                State_NotStarted = -3,
                State_FloatingRun = -2;

            public readonly LayoutEngine Engine;
            public readonly ControlKey Parent;
            private int _Current;
            private int _FloatingRun;
            private int Version;

            public RunEnumerator (LayoutEngine engine, ControlKey parent) {
                Engine = engine;
                Version = engine.Version;
                Parent = parent;
                _FloatingRun = -1;
                _Current = State_NotStarted;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void CheckVersion () {
                if (Version == Engine.Version)
                    return;

                Engine.AssertionFailed("Context was modified");
            }

            public int Current {
                [TargetedPatchingOptOut("")]
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => (_Current == State_FloatingRun) ? _FloatingRun : _Current;
            }
            object IEnumerator.Current => Current;

            public void Dispose () {
                _Current = State_Disposed;
                Version = -1;
            }

            public bool MoveNext () {
                CheckVersion();

                if (_Current >= 0) {
                    ref var run = ref Engine.Run(_Current);
                    _Current = run.NextRunIndex;
                    // TODO: Loop detection
                    return (Current >= 0);
                } else if (
                    (_Current != State_NotStarted) &&
                    (_Current != State_FloatingRun)
                ) {
                    return false;
                }

                if (Parent.IsInvalid)
                    return false;

                ref var rec = ref Engine.UnsafeResult(Parent);
                _FloatingRun = rec.FloatingRunIndex;
                if ((_FloatingRun >= 0) && (_Current == State_NotStarted)) {
                    _Current = State_FloatingRun;
                    return true;
                } else {
                    _Current = rec.FirstRunIndex;
                    return Current >= 0;
                }
            }

            void IEnumerator.Reset () {
                CheckVersion();
                _Current = State_NotStarted;
            }
        }

        public struct RunEnumerable : IEnumerable<int> {
            public readonly LayoutEngine Engine;
            public readonly ControlKey Parent;

            internal RunEnumerable (LayoutEngine engine, ControlKey parent) {
                Engine = engine;
                Parent = parent;
            }

            public RunEnumerator GetEnumerator () {
                return new RunEnumerator(Engine, Parent);
            }

            IEnumerator<int> IEnumerable<int>.GetEnumerator () {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator () {
                return GetEnumerator();
            }
        }

        // TODO: Reimplement this, we should be able to compute accurate content size during layout
        public bool TryMeasureContent (ControlKey container, out RectF result) {
            ref var pItem = ref this[container];
            float minX = 999999, minY = 999999,
                maxX = -999999, maxY = -999999;

            if (pItem.FirstChild.IsInvalid) {
                result = default(RectF);
                return true;
            }

            foreach (var ckey in Children(container)) {
                ref var child = ref this[ckey];
                ref var childResult = ref Result(ckey);
                var childRect = childResult.Rect;

                // HACK: The arrange algorithms will clip an element to its containing box, which
                //  hinders attempts to measure all of the content inside a container for scrolling
                child.Width.Constrain(ref childRect.Width, true);
                child.Height.Constrain(ref childRect.Height, true);

                minX = Math.Min(minX, childRect.Left - child.Margins.Left);
                maxX = Math.Max(maxX, childRect.Left + childRect.Width + child.Margins.Right);
                minY = Math.Min(minY, childRect.Top - child.Margins.Top);
                maxY = Math.Max(maxY, childRect.Top + childRect.Height + child.Margins.Bottom);
            }

            result = new RectF(minX, minY, maxX - minX, maxY - minY);
            return true;
        }
    }
}
