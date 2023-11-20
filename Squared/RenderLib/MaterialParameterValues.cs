﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Squared.Util;

namespace Squared.Render {

    public struct MaterialParameterValues : IEnumerable<KeyValuePair<string, object>> {
        // TODO: Store Texture and Array separately so we can fit more values more efficiently,
        //  since it's common to have a few textures + a few uniforms

        public struct Storage {
            internal UnorderedList<Key> Keys;
            internal UnorderedList<Value> Values;

            public Storage (ref MaterialParameterValues source) {
                Keys = source.Keys.GetStorage(true);
                Values = source.Values.GetStorage(true);
            }

            // FIXME: Is this right?
            public Storage EnsureUniqueStorage (ref MaterialParameterValues parameters) {
                if ((Keys != null) && (Values != null))
                    return this;
                else
                    return parameters.GetUniqueStorage();
            }
        }

        private sealed class KeyComparer : IRefComparer<Key> {
            public static readonly KeyComparer Instance = new KeyComparer();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare (ref Key lhs, ref Key rhs) {
                var result = lhs.HashCode.CompareTo(rhs.HashCode);
                if (result == 0)
                    result = string.CompareOrdinal(lhs.Name, rhs.Name);
                return result;
            }
        }

        [Flags]
        internal enum StateFlags {
            IsCleared = 0b001,
            CopyOnWrite = 0b010,
            // SortNeeded = 0b100,
        }

        internal enum EntryValueType : int {
            None,
            Texture,
            Array,
            B,
            F,
            I,
            V2,
            V3,
            V4,
            Q
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct EntryUnion {
            [FieldOffset(0)]
            public bool B;
            [FieldOffset(0)]
            public float F;
            [FieldOffset(0)]
            public int I;
            [FieldOffset(0)]
            public Vector2 V2;
            [FieldOffset(0)]
            public Vector3 V3;
            [FieldOffset(0)]
            public Vector4 V4;
            [FieldOffset(0)]
            public Quaternion Q;
        }

        internal struct Key {
            public int HashCode;
            public string Name;
            public int ValueIndex;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Key (string name) {
                HashCode = name.GetHashCode();
                Name = name;
                ValueIndex = -1;
            }

            public override string ToString () =>
                $"{Name} @{ValueIndex}";
        }

        internal struct Value {
            public EntryValueType Type;
            public object Reference;
            public EntryUnion Primitive;

            public object BoxedValue {
                get {
                    if (Reference != null)
                        return Reference;

                    switch (Type) {
                        case EntryValueType.B:
                            return Primitive.B;
                        case EntryValueType.F:
                            return Primitive.F;
                        case EntryValueType.I:
                            return Primitive.I;
                        case EntryValueType.V2:
                            return Primitive.V2;
                        case EntryValueType.V3:
                            return Primitive.V3;
                        case EntryValueType.V4:
                            return Primitive.V4;
                        case EntryValueType.Q:
                            return Primitive.Q;
                        default:
                            return null;
                    }
                }
            }

            public static bool Equals (ref Value lhs, ref Value rhs) {
                if (lhs.Type != rhs.Type)
                    return false;

                if (!ReferenceEquals(lhs.Reference, rhs.Reference))
                    return false;

                switch (lhs.Type) {
                    case EntryValueType.B:
                        return lhs.Primitive.B == rhs.Primitive.B;
                    case EntryValueType.F:
                        return lhs.Primitive.F == rhs.Primitive.F;
                    case EntryValueType.I:
                        return lhs.Primitive.I == rhs.Primitive.I;
                    case EntryValueType.V2:
                        return lhs.Primitive.V2 == rhs.Primitive.V2;
                    case EntryValueType.V3:
                        return lhs.Primitive.V3 == rhs.Primitive.V3;
                    case EntryValueType.V4:
                        return lhs.Primitive.V4 == rhs.Primitive.V4;
                    case EntryValueType.Q:
                        return lhs.Primitive.Q == rhs.Primitive.Q;
                    case EntryValueType.Texture:
                    case EntryValueType.Array:
                        return true;
                    default:
                        throw new ArgumentOutOfRangeException("lhs.ValueType");
                }
            }
        }

        private StateFlags State;
        private DenseList<Key> Keys;
        // TODO: Replace this with a few numbered object slots for references,
        //  and a big pile of UInt64s to pack primitives into. That will make
        //  it possible to fit more small parameters (bools, floats) into the
        //  available space before we need to allocate a backing store
        private DenseList<Value> Values;

        public bool AllocateNewStorageOnWrite {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetInternalFlag(StateFlags.CopyOnWrite);
            set => SetInternalFlag(StateFlags.CopyOnWrite, value);
        }

        public bool IsCleared {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetInternalFlag(StateFlags.IsCleared);
            private set => SetInternalFlag(StateFlags.IsCleared, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool GetInternalFlag (StateFlags flag) {
            return (State & flag) == flag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool? GetInternalFlag (StateFlags isSetFlag, StateFlags valueFlag) {
            if ((State & isSetFlag) != isSetFlag)
                return null;
            else
                return (State & valueFlag) == valueFlag;
        }

        private void SetInternalFlag (StateFlags flag, bool state) {
            if (state)
                State |= flag;
            else
                State &= ~flag;
        }

        private bool ChangeInternalFlag (StateFlags flag, bool newState) {
            if (GetInternalFlag(flag) == newState)
                return false;

            SetInternalFlag(flag, newState);
            return true;
        }

        public Storage GetUniqueStorage () {
            SetInternalFlag(StateFlags.CopyOnWrite, true);
            FlushCopyOnWrite();
            return new Storage(ref this);
        }

        public void UseExistingListStorage (Storage storage, bool preserveContents) {
            SetInternalFlag(StateFlags.CopyOnWrite, false);
            Keys.UseExistingStorage(storage.Keys, preserveContents);
            Values.UseExistingStorage(storage.Values, preserveContents);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void FlushCopyOnWrite () {
            if (!AllocateNewStorageOnWrite)
                return;

            AllocateNewStorageOnWrite = false;
            if (!Keys.HasList && !Values.HasList)
                return;

            FlushCopyOnWrite_Slow();
        }

        void FlushCopyOnWrite_Slow () {
            var oldKeys = Keys;
            var oldValues = Values;
            oldKeys.Clone(out Keys);
            oldValues.Clone(out Values);
        }
        
        public int Count {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                return IsCleared ? 0 : Keys.Count;
            }
        }

        public bool IsEmpty {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetInternalFlag(StateFlags.IsCleared) || (Keys.Count < 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool FindKey (string name, out int keyIndex, out int valueIndex) {
            var needle = new Key(name);
            return FindKey(ref needle, out keyIndex, out valueIndex);
        }

        private bool FindKey (ref Key needle, out int keyIndex, out int valueIndex) {
            keyIndex = valueIndex = -1;
            int count = Count;
            if (count <= 0)
                return false;

            /*
            if (ChangeInternalFlag(StateFlags.SortNeeded, false))
                Keys.Sort(KeyComparer.Instance);

            keyIndex = Keys.BinarySearch(ref needle, KeyComparer.Instance);
            if (keyIndex < 0)
                return false;
            */

            for (int i = 0; i < count; i++) {
                ref var key = ref Keys.Item(i);
                if (key.HashCode != needle.HashCode)
                    continue;
                if (!string.Equals(key.Name, needle.Name, StringComparison.Ordinal))
                    continue;

                keyIndex = i;
                valueIndex = key.ValueIndex;
                return true;
            }

            return false;
        }

        public void Clear () {
            if (Keys.Count <= 0)
                return;

            SetInternalFlag(StateFlags.IsCleared, true);
            // SetInternalFlag(StateFlags.SortNeeded, false);
        }

        public void AddRange (ref MaterialParameterValues rhs) {
            for (int i = 0, c = rhs.Count; i < c; i++) {
                ref var key = ref rhs.Keys.Item(i);
                ref var value = ref rhs.Values.Item(key.ValueIndex);
                Set(ref key, ref value);
            }
        }

        internal bool TryGet (string name, out Value result) {
            if (!FindKey(name, out _, out int valueIndex)) {
                result = default(Value);
                return false;
            }
            Values.GetItem(valueIndex, out result);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AutoClear () {
            if (!IsCleared)
                return;
            if (GetInternalFlag(StateFlags.CopyOnWrite)) {
                Keys = default;
                Values = default;
            } else {
                Keys.Clear();
                Values.Clear();
            }
            SetInternalFlag(StateFlags.IsCleared, false);
            // SetInternalFlag(StateFlags.SortNeeded, false);
        }

        public void ReplaceWith (ref MaterialParameterValues values) {
            if (values.Count == 0) {
                Clear();
                return;
            }

            SetInternalFlag(StateFlags.IsCleared, false);
            // SetInternalFlag(StateFlags.SortNeeded, false);
            FlushCopyOnWrite();
            // HACK: Keys will be interned strings 99% of the time, so leaking them doesn't matter
            Keys.ReplaceWith(ref values.Keys, false);
            // FIXME: Split VT and ref values into separate lists so we don't need to clear the VT list
            Values.ReplaceWith(ref values.Values);
        }

        private void Set (ref Key key, ref Value value) {
            AutoClear();
            if (FindKey(ref key, out _, out int valueIndex)) {
                ref var existingValue = ref Values.Item(valueIndex);
                if (Value.Equals(ref existingValue, ref value))
                    return;
                FlushCopyOnWrite();
                Values.SetItem(valueIndex, ref value);
                return;
            }

            FlushCopyOnWrite();
            key.ValueIndex = Count;

            /*
            if (key.ValueIndex > 0)
                SetInternalFlag(StateFlags.SortNeeded, true);
            */
            Keys.Add(ref key);
            Values.Add(ref value);
        }

        private void Set (string name, Value entry) {
            var key = new Key(name);
            Set(ref key, ref entry);
        }

        public void Add (string name, int value) {
            Set(name, new Value {
                Type = EntryValueType.I,
                Primitive = {
                    I = value
                }
            });
        }

        public void Add (string name, float value) {
            Set(name, new Value {
                Type = EntryValueType.F,
                Primitive = {
                    F = value
                }
            });
        }

        public void Add (string name, Color value) {
            Set(name, new Value {
                Type = EntryValueType.V4,
                Primitive = {
                    V4 = value.ToVector4()
                }
            });
        }

        public void Add (string name, bool value) {
            Set(name, new Value {
                Type = EntryValueType.B,
                Primitive = {
                    B = value
                }
            });
        }

        public void Add (string name, Vector2 value) {
            Set(name, new Value {
                Type = EntryValueType.V2,
                Primitive = {
                    V2 = value
                }
            });
        }

        public void Add (string name, Vector3 value) {
            Set(name, new Value {
                Type = EntryValueType.V3,
                Primitive = {
                    V3 = value
                }
            });
        }

        public void Add (string name, Vector4 value) {
            Set(name, new Value {
                Type = EntryValueType.V4,
                Primitive = {
                    V4 = value
                }
            });
        }

        public void Add (string name, Quaternion value) {
            Set(name, new Value {
                Type = EntryValueType.Q,
                Primitive = {
                    Q = value
                }
            });
        }

        public void Add (string name, Texture texture) {
            Set(name, new Value {
                Type = EntryValueType.Texture,
                Reference = texture
            });
        }

        public void Add (string name, Array array) {
            Set(name, new Value {
                Type = EntryValueType.Array,
                Reference = array
            });
        }

        public void Apply (Material material) {
            if (material.Effect == null)
                return;
            Apply(material.Effect, material.Parameters);
        }

        private void Apply (Effect effect, MaterialEffectParameters cache) {
            for (int i = 0, c = Count; i < c; i++) {
                ref var key = ref Keys.Item(i);
                var p = cache[key.Name];
                if (p == null)
                    continue;
                ApplyEntry(ref Values.Item(key.ValueIndex), p);
            }
        }

        private static void ApplyEntry (ref Value entry, EffectParameter p) {
            var r = entry.Reference;
            switch (entry.Type) {
                case EntryValueType.Texture:
                    p.SetValue((Texture)r);
                    break;
                case EntryValueType.Array:
                    if (r is float[] fa)
                        p.SetValue(fa);
                    else if (r is int[] ia)
                        p.SetValue(ia);
                    else if (r is bool[] ba)
                        p.SetValue(ba);
                    else if (r is Matrix[] ma)
                        p.SetValue(ma);
                    else if (r is Vector2[] v2a)
                        p.SetValue(v2a);
                    else if (r is Vector3[] v3a)
                        p.SetValue(v3a);
                    else if (r is Vector4[] v4a)
                        p.SetValue(v4a);
                    else if (r is Quaternion[] qa)
                        p.SetValue(qa);
                    else
                        throw new ArgumentException("Unsupported array parameter type");
                    break;
                case EntryValueType.B:
                    p.SetValue(entry.Primitive.B);
                    break;
                case EntryValueType.F:
                    p.SetValue(entry.Primitive.F);
                    break;
                case EntryValueType.I:
                    p.SetValue(entry.Primitive.I);
                    break;
                case EntryValueType.V2:
                    p.SetValue(entry.Primitive.V2);
                    break;
                case EntryValueType.V3:
                    p.SetValue(entry.Primitive.V3);
                    break;
                case EntryValueType.V4:
                    p.SetValue(entry.Primitive.V4);
                    break;
                case EntryValueType.Q:
                    p.SetValue(entry.Primitive.Q);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("entry.ValueType");
            }
        }

        public bool Equals (MaterialParameterValues pRhs) => Equals(ref pRhs);

        public bool Equals (ref MaterialParameterValues pRhs) {
            var count = Count;
            if (count != pRhs.Count)
                return false;
            if (count == 0)
                return true;

            for (int i = 0; i < count; i++) {
                ref var lhsKey = ref Keys.Item(i);
                if (!pRhs.FindKey(ref lhsKey, out _, out int rhsValueIndex))
                    return false;
                if (!Value.Equals(ref Values.Item(lhsKey.ValueIndex), ref pRhs.Values.Item(rhsValueIndex)))
                    return false;
            }

            return true;
        }

        public override int GetHashCode () {
            return Count;
        }

        public override bool Equals (object obj) {
            if (obj is MaterialParameterValues mpv)
                return Equals(ref mpv);
            else
                return false;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator () {
            foreach (var key in Keys)
                yield return new KeyValuePair<string, object>(key.Name, Values[key.ValueIndex].BoxedValue);
        }

        IEnumerator IEnumerable.GetEnumerator () {
            return GetEnumerator();
        }

        internal void CopyTo (ref MaterialParameterValues rhs) {
            int count = Count;
            if (count == 0)
                return;
            for (int i = 0; i < count; i++) {
                ref var key = ref Keys.Item(i);
                rhs.Set(ref key, ref Values.Item(key.ValueIndex));
            }
        }
    }
}
