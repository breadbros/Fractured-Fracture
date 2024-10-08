﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Squared.Util.Text {
    public static class Unicode {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool DecodeSurrogatePair (char ch1, char ch2, out uint codepoint) {
            codepoint = (uint)ch1;
            if (char.IsSurrogatePair(ch1, ch2)) {
                codepoint = (uint)char.ConvertToUtf32(ch1, ch2);
                return true;
            } else if (char.IsHighSurrogate(ch1) || char.IsLowSurrogate(ch1)) {
                // if we have a corrupt partial surrogate pair, it's not meaningful to return the first half.
                codepoint = 0xFFFD;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Next (this AbstractString str, int offset) {
            var ch = str[offset];
            if (char.IsHighSurrogate(ch))
                return offset + 2;
            else
                return offset + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Previous (this AbstractString str, int offset) {
            var ch = str[offset - 1];
            if (char.IsLowSurrogate(ch))
                return offset - 2;
            else
                return offset - 1;
        }

        public static CodepointEnumerable Codepoints (this AbstractString str, int startOffset = 0) {
            return new CodepointEnumerable(str, startOffset);
        }

        public static CodepointEnumerable Codepoints (this string str, int startOffset = 0) {
            return new CodepointEnumerable(str, startOffset);
        }

        public static CodepointEnumerable Codepoints (this StringBuilder str, int startOffset = 0) {
            return new CodepointEnumerable(str, startOffset);
        }

        public static CodepointEnumerable Codepoints (this ArraySegment<char> str, int startOffset = 0) {
            return new CodepointEnumerable(str, startOffset);
        }

        public static uint? NthCodepoint (AbstractString str, int codepointIndex, int relativeToCharacterIndex = 0) {
            foreach (var cp in str.Codepoints(relativeToCharacterIndex)) {
                if (codepointIndex == 0)
                    return cp.Codepoint;
                codepointIndex--;
            }

            return null;
        }

        public static bool IsWhiteSpace (uint codepoint) {
            if (codepoint > 0xFFFF)
                return false;
            else
                return char.IsWhiteSpace((char)codepoint);
        }

        public static Pair<int> FindWordBoundary (AbstractString str, int? searchFromCodepointIndex = null, int? searchFromCharacterIndex = null) {
            int firstWhitespaceCharacter = -1, 
                lastWhitespaceCharacter = -1, 
                firstWordCharacter = -1, 
                lastWordCharacter = -1;

            if ((searchFromCharacterIndex == null) && (searchFromCodepointIndex == null))
                throw new ArgumentException("Either a starting codepoint index or character index must be provided");

            bool searchStartedInWhiteSpace = false, inWord = false;
            foreach (var cp in str.Codepoints()) {
                bool transitioned = false;
                var isWhiteSpace = IsWhiteSpace(cp.Codepoint);
                if (
                    (cp.CodepointIndex == searchFromCodepointIndex) ||
                    (cp.CharacterIndex == searchFromCharacterIndex)
                )
                    searchStartedInWhiteSpace = isWhiteSpace;

                if (isWhiteSpace) {
                    if (inWord || firstWhitespaceCharacter < 0) {
                        transitioned = inWord;
                        inWord = false;
                        firstWhitespaceCharacter = cp.CharacterIndex;
                    }
                    lastWhitespaceCharacter = cp.CharacterIndex;
                } else {
                    if (!inWord || firstWordCharacter < 0) {
                        transitioned = !inWord;
                        inWord = true;
                        firstWordCharacter = cp.CharacterIndex;
                    }
                    lastWordCharacter = cp.CharacterIndex;
                }

                if (transitioned && 
                    (
                        (searchFromCodepointIndex.HasValue && (cp.CodepointIndex > searchFromCodepointIndex)) ||
                        (searchFromCharacterIndex.HasValue && (cp.CharacterIndex > searchFromCharacterIndex))
                    )
                )
                    break;
            }

            if (searchStartedInWhiteSpace)
                return new Pair<int>(firstWhitespaceCharacter, lastWhitespaceCharacter + 1);
            else {
                if ((lastWordCharacter > 0) && char.IsHighSurrogate(str[lastWordCharacter]))
                    lastWordCharacter++;
                return new Pair<int>(firstWordCharacter, lastWordCharacter + 1);
            }
        }
    }

    public struct CodepointEnumerable : IEnumerable<CodepointEnumerant> {
        public AbstractString String;
        public int StartOffset;

        public CodepointEnumerable (AbstractString str, int startOffset = 0) {
            String = str;
            StartOffset = startOffset;
        }

        public CodepointEnumerator GetEnumerator () {
            return new CodepointEnumerator(String, StartOffset);
        }

        IEnumerator IEnumerable.GetEnumerator () {
            return new CodepointEnumerator(String, StartOffset);
        }

        IEnumerator<CodepointEnumerant> IEnumerable<CodepointEnumerant>.GetEnumerator () {
            return new CodepointEnumerator(String, StartOffset);
        }
    }

    public struct CodepointEnumerant {
        public int CharacterIndex, CodepointIndex;
        public uint Codepoint;

        public static explicit operator uint (CodepointEnumerant value) {
            return value.Codepoint;
        }
    }

    public struct CodepointEnumerator : IEnumerator<CodepointEnumerant> {
        public AbstractString String;
        private int Length;
        private int Offset, StartOffset, _CurrentCharacterIndex, _CurrentCodepointIndex;
        private uint _CurrentCodepoint;
        private bool InSurrogatePair;

        public CodepointEnumerator (AbstractString str, int startOffset) {
            String = str;
            Length = str.Length;
            StartOffset = startOffset;
            Offset = startOffset - 1;
            _CurrentCharacterIndex = startOffset - 1;
            _CurrentCodepointIndex = -1;
            _CurrentCodepoint = 0;
            InSurrogatePair = false;
        }

        public CodepointEnumerant Current => new CodepointEnumerant {
            CharacterIndex = _CurrentCharacterIndex,
            CodepointIndex = _CurrentCodepointIndex,
            Codepoint = _CurrentCodepoint
        };
        object IEnumerator.Current => Current;

        public void Dispose () {
            String = default(AbstractString);
            Offset = -1;
            _CurrentCharacterIndex = -1;
            _CurrentCodepointIndex = -1;
            _CurrentCodepoint = 0;
            InSurrogatePair = false;
        }

        public bool MoveNext () {
            Offset++;
            if (Offset >= Length)
                return false;

            if (InSurrogatePair)
                _CurrentCharacterIndex++;

            char ch1 = String[Offset],
                ch2 = (Offset >= Length - 1)
                    ? '\0' : String[Offset + 1];
            if (Unicode.DecodeSurrogatePair(ch1, ch2, out _CurrentCodepoint)) {
                Offset++;
                InSurrogatePair = true;
            } else {
                InSurrogatePair = false;
            }

            _CurrentCodepointIndex++;
            _CurrentCharacterIndex++;

            return true;
        }

        public void Reset () {
            Length = String.Length;
            Offset = StartOffset - 1;
            _CurrentCharacterIndex = StartOffset - 1;
            _CurrentCodepointIndex = -1;
            _CurrentCodepoint = 0;
            InSurrogatePair = false;
        }
    }

    public struct AbstractString : IEquatable<AbstractString> {
        private readonly string String;
        private readonly StringBuilder StringBuilder;
        private readonly ArraySegment<char> ArraySegment;
        private readonly int SubstringOffset, SubstringLength;

        public bool IsArraySegment {
            get {
                return (ArraySegment.Array != null);
            }
        }

        public bool IsString {
            get {
                return (String != null);
            }
        }

        public bool IsStringBuilder {
            get {
                return (StringBuilder != null);
            }
        }

        public AbstractString (string text, int substringOffset = 0, int substringLength = 0) {
            String = text;
            StringBuilder = null;
            // HACK: Make this easy to use
            SubstringOffset = Math.Min(substringOffset, text?.Length ?? 0);
            SubstringLength = Math.Min(substringLength, text?.Length ?? 0);
            ArraySegment = default(ArraySegment<char>);
        }

        public AbstractString (StringBuilder stringBuilder, int substringOffset = 0, int substringLength = 0) {
            String = null;
            StringBuilder = stringBuilder;
            SubstringOffset = substringOffset;
            SubstringLength = substringLength;
            ArraySegment = default(ArraySegment<char>);
        }

        public AbstractString (char[] array) {
            String = null;
            StringBuilder = null;
            SubstringOffset = 0;
            SubstringLength = 0;
            ArraySegment = new ArraySegment<char>(array);
        }

        public AbstractString (ArraySegment<char> array) {
            String = null;
            StringBuilder = null;
            SubstringOffset = 0;
            SubstringLength = 0;
            ArraySegment = array;
        }

        public static implicit operator AbstractString (string text) {
            return new AbstractString(text);
        }

        public static implicit operator AbstractString (StringBuilder stringBuilder) {
            return new AbstractString(stringBuilder);
        }

        public static implicit operator AbstractString (char[] array) {
            return new AbstractString(array);
        }

        public static implicit operator AbstractString (ArraySegment<char> array) {
            return new AbstractString(array);
        }

        public bool Equals (AbstractString other) {
            return (
                object.ReferenceEquals(String, other.String) &&
                object.ReferenceEquals(StringBuilder, other.StringBuilder) &&
                (SubstringOffset == other.SubstringOffset) &&
                (SubstringLength == other.SubstringLength) &&
                (ArraySegment == other.ArraySegment)
            );
        }

        public bool TextEquals (string other) {
            if (Length != other?.Length)
                return false;
            return string.Equals(ToString(), other);
        }

        public bool TextEquals (string other, StringComparison comparison) {
            if (Length != other?.Length)
                return false;
            return string.Equals(ToString(), other, comparison);
        }

        public bool TextEquals (AbstractString other, StringComparison comparison) {
            if (Equals(other))
                return true;
            if (Length != other.Length)
                return false;
            // FIXME: Optimize this
            return string.Equals(ToString(), other.ToString(), comparison);
        }

        public override int GetHashCode () {
            return (String?.GetHashCode() ?? 0) ^
                (StringBuilder?.GetHashCode() ?? 0) ^
                (ArraySegment.Array?.GetHashCode() ?? 0);
        }

        public override bool Equals (object obj) {
            if (obj is string)
                return TextEquals((string)obj);
            else if (obj is AbstractString)
                return Equals((AbstractString)obj);
            else
                return false;
        }

        // FIXME: Should these be TextEquals?
        public static bool operator == (AbstractString lhs, AbstractString rhs) {
            return lhs.Equals(rhs);
        }

        public static bool operator != (AbstractString lhs, AbstractString rhs) {
            return !lhs.Equals(rhs);
        }

        public static bool operator == (AbstractString lhs, string rhs) {
            return lhs.TextEquals(rhs);
        }

        public static bool operator != (AbstractString lhs, string rhs) {
            return !lhs.TextEquals(rhs);
        }

        public char this[int index] {
            get {
                if (String != null)
                    return String[index + SubstringOffset];
                else if (StringBuilder != null)
                    return StringBuilder[index + SubstringOffset];
                else if (ArraySegment.Array != null) {
                    if ((index < 0) || (index >= ArraySegment.Count))
                        throw new ArgumentOutOfRangeException("index");

                    return ArraySegment.Array[index + ArraySegment.Offset];
                } else
                    throw new NullReferenceException("This string contains no text");
            }
        }

        public int Length {
            get {
                if (String != null)
                    return (SubstringLength > 0) ? SubstringLength : (String.Length - SubstringOffset);
                else if (StringBuilder != null)
                    return (SubstringLength > 0) ? SubstringLength : (StringBuilder.Length - SubstringOffset);
                else // Default fallback to 0 characters
                    return ArraySegment.Count;
            }
        }

        public bool IsNull {
            get {
                return
                    (String == null) &&
                    (StringBuilder == null) &&
                    (ArraySegment.Array == null);
            }
        }

        private string ConvertStringInternal () {
            if ((SubstringOffset <= 0) && (SubstringLength <= 0))
                return String;
            else if (SubstringLength <= 0)
                return String.Substring(SubstringOffset);
            else
                return String.Substring(SubstringOffset, SubstringLength);
        }

        private string ConvertBuilderInternal () {
            if ((SubstringOffset <= 0) && (SubstringLength <= 0))
                return StringBuilder.ToString();
            else if (SubstringLength <= 0)
                return StringBuilder.ToString(SubstringOffset, StringBuilder.Length - SubstringOffset);
            else
                return StringBuilder.ToString(SubstringOffset, SubstringLength);
        }

        public override string ToString () {
            if (String != null)
                return ConvertStringInternal();
            else if (StringBuilder != null)
                return ConvertBuilderInternal();
            else if (ArraySegment.Array != null)
                return new string(ArraySegment.Array, ArraySegment.Offset, ArraySegment.Count);
            else
                return null;
        }

        public bool Contains (char ch) {
            if (String != null)
                return String.IndexOf(ch, SubstringOffset, Length) >= 0;

            for (int i = 0, l = Length; i < l; i++) {
                if (this[i] == ch)
                    return true;
            }

            return false;
        }

        public string Substring (int start, int count) {
            if (String != null)
                return String.Substring(start, count);
            else if (StringBuilder != null)
                return StringBuilder.ToString(start, count);
            else if (ArraySegment.Array != null)
                return new string(ArraySegment.Array, ArraySegment.Offset + start, count);
            else
                throw new ArgumentNullException("this");
        }

        public void CopyTo (StringBuilder output) {
            if (String != null)
                output.Append(ConvertStringInternal());
            else if (StringBuilder != null) {
                if ((SubstringOffset <= 0) && (SubstringLength <= 0))
                    StringBuilder.CopyTo(output);
                else
                    StringBuilder.Append(ConvertBuilderInternal());
            } else if (ArraySegment.Array != null)
                output.Append(ArraySegment.Array, ArraySegment.Offset, ArraySegment.Count);
            else
                throw new ArgumentNullException("this");
        }
    }

    public static class TextExtensions {
        public static void CopyTo (this StringBuilder source, StringBuilder destination) {
            using (var buffer = BufferPool<char>.Allocate(source.Length)) {
                source.CopyTo(0, buffer.Data, 0, source.Length);
                destination.Append(buffer.Data, 0, source.Length);
            }
        }
    }
}
