﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Squared.Render.Evil;
using Squared.Render.Text;
using Squared.Util;
using Microsoft.Xna.Framework.Graphics;
using Squared.Game;
using Microsoft.Xna.Framework;
using System.Reflection;
using Squared.Util.Text;
using System.Globalization;

namespace Squared.Render.Text {
    public struct StringLayout {
        private static readonly ConditionalWeakTable<SpriteFont, Dictionary<char, KerningAdjustment>> _DefaultKerningAdjustments =
            new ConditionalWeakTable<SpriteFont, Dictionary<char, KerningAdjustment>>(); 

        public readonly Vector2 Position;
        /// <summary>
        /// The size of the layout's visible characters in their wrapped positions.
        /// </summary>
        public readonly Vector2 Size;
        /// <summary>
        /// The size that the layout would have had if it was unconstrained by wrapping and character/line limits.
        /// </summary>
        public readonly Vector2 UnconstrainedSize;
        public readonly float LineHeight;
        public readonly Bounds FirstCharacterBounds;
        public readonly Bounds LastCharacterBounds;
        public ArraySegment<BitmapDrawCall> DrawCalls;
        public DenseList<AbstractTextureReference> UsedTextures;
        public DenseList<Bounds> Boxes;
        public readonly bool WasLineLimited;

        public StringLayout (
            Vector2 position, Vector2 size, Vector2 unconstrainedSize, 
            float lineHeight, Bounds firstCharacter, Bounds lastCharacter, 
            ArraySegment<BitmapDrawCall> drawCalls, bool wasLineLimited
        ) {
            Position = position;
            Size = size;
            UnconstrainedSize = unconstrainedSize;
            LineHeight = lineHeight;
            FirstCharacterBounds = firstCharacter;
            LastCharacterBounds = lastCharacter;
            DrawCalls = drawCalls;
            WasLineLimited = wasLineLimited;
            Boxes = default(DenseList<Bounds>);
            UsedTextures = default(DenseList<AbstractTextureReference>);
        }

        public int Count {
            get {
                return DrawCalls.Count;
            }
        }

        public BitmapDrawCall this[int index] {
            get {
                if ((index < 0) || (index >= Count))
                    throw new ArgumentOutOfRangeException("index");

                return DrawCalls.Array[DrawCalls.Offset + index];
            }
            set {
                if ((index < 0) || (index >= Count))
                    throw new ArgumentOutOfRangeException("index");

                DrawCalls.Array[DrawCalls.Offset + index] = value;
            }
        }

        public ArraySegment<BitmapDrawCall> Slice (int skip, int count) {
            return new ArraySegment<BitmapDrawCall>(
                DrawCalls.Array, DrawCalls.Offset + skip, Math.Max(Math.Min(count, DrawCalls.Count - skip), 0)
            );
        }

        public static implicit operator ArraySegment<BitmapDrawCall> (StringLayout layout) {
            return layout.DrawCalls;
        }

        public static Dictionary<char, KerningAdjustment> GetDefaultKerningAdjustments (IGlyphSource font) {
            // FIXME
            if (font is SpriteFontGlyphSource) {
                Dictionary<char, KerningAdjustment> result;
                _DefaultKerningAdjustments.TryGetValue(((SpriteFontGlyphSource)font).Font, out result);
                return result;
            } else {
                return null;
            }
        }

        public static void SetDefaultKerningAdjustments (SpriteFont font, Dictionary<char, KerningAdjustment> adjustments) {
            _DefaultKerningAdjustments.Remove(font);
            _DefaultKerningAdjustments.Add(font, adjustments);
        }
    }

    public struct KerningAdjustment {
        public float LeftSideBearing, RightSideBearing, Width;

        public KerningAdjustment (float leftSide = 0f, float rightSide = 0f, float width = 0f) {
            LeftSideBearing = leftSide;
            RightSideBearing = rightSide;
            Width = width;
        }
    }

    public enum HorizontalAlignment : int {
        Left,
        Center,
        Right,
        // Justify
    }

    public struct LayoutMarker {
        public class Comparer : IRefComparer<LayoutMarker> {
            public static readonly Comparer Instance = new Comparer();

            public int Compare (ref LayoutMarker lhs, ref LayoutMarker rhs) {
                var result = lhs.FirstCharacterIndex.CompareTo(rhs.FirstCharacterIndex);
                if (result == 0)
                    result = lhs.LastCharacterIndex.CompareTo(rhs.LastCharacterIndex);
                return result;
            }
        }

        public AbstractString MarkedString;
        public string MarkedID;
        public int FirstCharacterIndex, LastCharacterIndex;
        public int? FirstDrawCallIndex, LastDrawCallIndex;
        public int GlyphCount;
        internal int CurrentSplitGlyphCount;
        public DenseList<Bounds> Bounds;

        public LayoutMarker (int firstIndex, int lastIndex, AbstractString markedString = default(AbstractString), string markedID = null) {
            FirstCharacterIndex = firstIndex;
            LastCharacterIndex = lastIndex;
            MarkedString = markedString;
            MarkedID = markedID;
            FirstDrawCallIndex = LastDrawCallIndex = null;
            GlyphCount = 0;
            CurrentSplitGlyphCount = 0;
            Bounds = default(DenseList<Bounds>);
        }

        public Bounds UnionBounds {
            get {
                if (Bounds.Count <= 1)
                    return Bounds.LastOrDefault();
                var b = Bounds[0];
                for (int i = 1; i < Bounds.Count; i++)
                    b = Squared.Game.Bounds.FromUnion(b, Bounds[i]);
                return b;
            }
        }

        public override string ToString () {
            return $"{MarkedID ?? "marker"} [{FirstCharacterIndex} - {LastCharacterIndex}] -> [{FirstDrawCallIndex} - {LastDrawCallIndex}] {Bounds.FirstOrDefault()}";
        }
    }

    public struct LayoutHitTest {
        public class Comparer : IRefComparer<LayoutHitTest> {
            public static readonly Comparer Instance = new Comparer();

            public int Compare (ref LayoutHitTest lhs, ref LayoutHitTest rhs) {
                var result = lhs.Position.X.CompareTo(rhs.Position.X);
                if (result == 0)
                    result = lhs.Position.Y.CompareTo(rhs.Position.Y);
                return result;
            }
        }

        public object Tag;
        public Vector2 Position;
        public int? FirstCharacterIndex, LastCharacterIndex;
        public bool LeaningRight;

        public LayoutHitTest (Vector2 position, object tag = null) {
            Position = position;
            Tag = tag;
            FirstCharacterIndex = LastCharacterIndex = null;
            LeaningRight = false;
        }

        public override string ToString () {
            return $"{Tag ?? "hitTest"} {Position} -> {FirstCharacterIndex} leaning {(LeaningRight ? "right" : "left")}";
        }
    }

    public struct StringLayoutEngine : IDisposable {
        public DenseList<LayoutMarker> Markers;
        public DenseList<LayoutHitTest> HitTests;
        public DenseList<uint> WordWrapCharacters;

        public const int DefaultBufferPadding = 4;

        // Parameters
        public UnorderedList<BitmapDrawCall>.Allocator allocator;
        public ArraySegment<BitmapDrawCall> buffer;
        public Vector2?            position;
        public Color?              overrideColor;
        public Color               defaultColor;
        public float               scale;
        private float              _spacingMinusOne;
        public DrawCallSortKey     sortKey;
        public int                 characterSkipCount;
        public int?                characterLimit;
        public float               xOffsetOfFirstLine;
        public float               xOffsetOfWrappedLine;
        public float               xOffsetOfNewLine;
        public float?              lineBreakAtX;
        public float?              stopAtY;
        public float               extraLineBreakSpacing;
        public bool                characterWrap;
        public bool                wordWrap;
        // FIXME: This isn't implemented?
        public char                wrapCharacter;
        public bool                hideOverflow;
        public bool                reverseOrder;
        public int?                lineLimit;
        public int?                lineBreakLimit;
        public bool                measureOnly;
        public bool                recordUsedTextures;
        public GlyphPixelAlignment alignToPixels;
        public HorizontalAlignment alignment;
        public uint?               replacementCodepoint;
        public Func<ArraySegment<BitmapDrawCall>, ArraySegment<BitmapDrawCall>> growBuffer;

        public float spacing {
            get {
                return _spacingMinusOne + 1;
            }
            set {
                _spacingMinusOne = value - 1;
            }
        }

        // State
        public float   maxLineHeight;
        public Vector2 actualPosition, characterOffset, characterOffsetUnconstrained;
        public Bounds  firstCharacterBounds, lastCharacterBounds;
        public int     drawCallsWritten, drawCallsSuppressed;
        float          initialLineXOffset;
        int            bufferWritePosition, wordStartWritePosition, baselineAdjustmentStart;
        public int     rowIndex { get; private set; }
        public int     colIndex { get; private set; }
        bool           wordWrapSuppressed;
        public float   currentLineMaxX, currentLineMaxXUnconstrained;
        public float?  currentLineBreakAtX;
        float          currentLineWrapPointLeft, currentLineWhitespaceMaxX;
        float          maxX, maxY, maxXUnconstrained, maxYUnconstrained;
        float          initialLineSpacing, currentLineSpacing;
        float          currentXOverhang;
        float          currentBaseline;
        float          maxLineSpacing;
        Vector2        wordStartOffset;
        private bool   ownsBuffer, suppress, suppressUntilNextLine, previousGlyphWasDead;
        private AbstractTextureReference lastUsedTexture;
        private DenseList<AbstractTextureReference> usedTextures;
        private DenseList<Bounds> boxes;

        public int currentCharacterIndex { get; private set; }

        private bool IsInitialized;

        public void Initialize () {
            actualPosition = position.GetValueOrDefault(Vector2.Zero);
            characterOffsetUnconstrained = characterOffset = new Vector2(xOffsetOfFirstLine, 0);
            initialLineXOffset = characterOffset.X;

            previousGlyphWasDead = suppress = suppressUntilNextLine = false;

            bufferWritePosition = 0;
            drawCallsWritten = 0;
            drawCallsSuppressed = 0;
            wordStartWritePosition = -1;
            wordStartOffset = Vector2.Zero;
            rowIndex = colIndex = 0;
            wordWrapSuppressed = false;
            initialLineSpacing = 0;
            currentBaseline = 0;
            currentLineSpacing = 0;
            maxLineSpacing = 0;
            currentXOverhang = 0;

            HitTests.Sort(LayoutHitTest.Comparer.Instance);
            for (int i = 0; i < HitTests.Count; i++) {
                var ht = HitTests[i];
                ht.FirstCharacterIndex = null;
                ht.LastCharacterIndex = null;
                HitTests[i] = ht;
            }

            Markers.Sort(LayoutMarker.Comparer.Instance);
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                m.Bounds.Clear();
                Markers[i] = m;
            }

            currentCharacterIndex = 0;
            lastUsedTexture = null;
            usedTextures = default(DenseList<AbstractTextureReference>);
            boxes = default(DenseList<Bounds>);
            ComputeLineBreakAtX();

            IsInitialized = true;
        }

        private void ProcessHitTests (ref Bounds bounds, float centerX) {
            var characterIndex = currentCharacterIndex;
            for (int i = 0; i < HitTests.Count; i++) {
                var ht = HitTests[i];
                if (bounds.Contains(ht.Position)) {
                    if (!ht.FirstCharacterIndex.HasValue) {
                        ht.FirstCharacterIndex = characterIndex;
                        // FIXME: Why is this literally always wrong?
                        ht.LeaningRight = (ht.Position.X >= centerX);
                    }
                    ht.LastCharacterIndex = characterIndex;
                    HitTests[i] = ht;
                }
            }
        }

        private void ProcessMarkers (ref Bounds bounds, int currentCodepointSize, int? drawCallIndex, bool splitMarker, bool didWrapWord) {
            if (measureOnly)
                return;
            if (suppress || suppressUntilNextLine)
                return;

            var characterIndex1 = currentCharacterIndex - currentCodepointSize + 1;
            var characterIndex2 = currentCharacterIndex;
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                if (m.FirstCharacterIndex > characterIndex2)
                    continue;
                if (m.LastCharacterIndex < characterIndex1)
                    continue;
                var curr = m.Bounds.LastOrDefault();
                if (curr != default(Bounds)) {
                    if (splitMarker && !didWrapWord) {
                        var newBounds = bounds;
                        if (m.CurrentSplitGlyphCount > 0) {
                            newBounds.TopLeft.X = Math.Min(curr.BottomRight.X, bounds.TopLeft.X);
                            newBounds.TopLeft.Y = Math.Min(curr.TopLeft.Y, bounds.TopLeft.Y);
                        }
                        m.CurrentSplitGlyphCount = 0;
                        m.Bounds.Add(newBounds);
                    } else if (didWrapWord && splitMarker && (m.CurrentSplitGlyphCount == 0)) {
                        m.Bounds[m.Bounds.Count - 1] = bounds;
                    } else {
                        var newBounds = Bounds.FromUnion(bounds, curr);
                        m.Bounds[m.Bounds.Count - 1] = newBounds;
                    }
                } else if (bounds != default(Bounds))
                    m.Bounds.Add(bounds);

                if (drawCallIndex != null) {
                    m.GlyphCount++;
                    m.CurrentSplitGlyphCount++;
                }

                m.FirstDrawCallIndex = m.FirstDrawCallIndex ?? drawCallIndex;
                m.LastDrawCallIndex = drawCallIndex ?? m.LastDrawCallIndex;
                Markers[i] = m;
            }
        }

        private void ProcessLineSpacingChange (ArraySegment<BitmapDrawCall> buffer, float newLineSpacing, float newBaseline) {
            if (newBaseline > currentBaseline) {
                if (bufferWritePosition > baselineAdjustmentStart) {
                    var yOffset = newBaseline - currentBaseline;
                    for (int i = baselineAdjustmentStart; i < bufferWritePosition; i++) {
                        buffer.Array[buffer.Offset + i].Position.Y += yOffset * (1 - buffer.Array[buffer.Offset + i].UserData.W);
                    }

                    if (!measureOnly) {
                        for (int i = 0; i < Markers.Count; i++) {
                            var m = Markers[i];
                            if (m.Bounds.Count <= 0)
                                continue;
                            if (m.FirstCharacterIndex > bufferWritePosition)
                                continue;
                            if (m.LastCharacterIndex < baselineAdjustmentStart)
                                continue;
                            // FIXME
                            var b = m.Bounds.LastOrDefault();
                            b.TopLeft.Y += yOffset;
                            b.BottomRight.Y += yOffset;
                            m.Bounds[m.Bounds.Count - 1] = b;
                            Markers[i] = m;
                        }
                    }
                }
                currentBaseline = newBaseline;
                baselineAdjustmentStart = bufferWritePosition;
            }

            if (newLineSpacing > currentLineSpacing)
                currentLineSpacing = newLineSpacing;

            ComputeLineBreakAtX();
        }

        private void WrapWord (
            ArraySegment<BitmapDrawCall> buffer,
            Vector2 firstOffset, int firstGlyphIndex, int lastGlyphIndex, 
            float glyphLineSpacing, float glyphBaseline, float currentWordSize
        ) {
            // FIXME: Can this ever happen?
            if (currentLineWhitespaceMaxX <= 0)
                maxX = Math.Max(maxX, currentLineMaxX);
            else
                maxX = Math.Max(maxX, currentLineWrapPointLeft);

            var previousLineSpacing = currentLineSpacing;
            var previousBaseline = currentBaseline;

            currentBaseline = glyphBaseline;
            initialLineSpacing = currentLineSpacing = glyphLineSpacing;

            // Remove the effect of the previous baseline adjustment then realign to our new baseline
            var yOffset = -previousBaseline + previousLineSpacing + currentBaseline;

            var suppressedByLineLimit = lineLimit.HasValue && (lineLimit.Value <= 0);
            var adjustment = Vector2.Zero;

            var xOffset = xOffsetOfWrappedLine;
            AdjustCharacterOffsetForBoxes(ref xOffset, characterOffset.Y + yOffset, currentLineSpacing, leftPad: 0f);
            var oldFirstGlyphBounds = (firstGlyphIndex > 0)
                ? buffer.Array[buffer.Offset + firstGlyphIndex - 1].EstimateDrawBounds()
                : default(Bounds);

            float wordX1 = 0, wordX2 = 0;

            for (var i = firstGlyphIndex; i <= lastGlyphIndex; i++) {
                var dc = buffer.Array[buffer.Offset + i];
                if (dc.UserData.Y > 0)
                    continue;
                var newCharacterX = (xOffset) + (dc.Position.X - firstOffset.X);
                if (i == firstGlyphIndex)
                    wordX1 = dc.Position.X;

                // FIXME: Baseline?
                var newPosition = new Vector2(newCharacterX, dc.Position.Y + yOffset);
                if (i == firstGlyphIndex)
                    adjustment = newPosition - dc.Position;
                dc.Position = newPosition;
                if (alignment != HorizontalAlignment.Left)
                    dc.SortOrder += 1;

                if (i == lastGlyphIndex) {
                    var db = dc.EstimateDrawBounds();
                    wordX2 = db.BottomRight.X;
                }

                if (suppressedByLineLimit && hideOverflow)
                    // HACK: Just setting multiplycolor or scale etc isn't enough since a layout filter may modify it
                    buffer.Array[buffer.Offset + i] = default(BitmapDrawCall);
                else
                    buffer.Array[buffer.Offset + i] = dc;
            }

            // FIXME: If we hit a box on the right edge, this is broken
            characterOffset.X = xOffset + (characterOffset.X - firstOffset.X);
            characterOffset.Y += previousLineSpacing;

            // HACK: firstOffset may include whitespace so we want to pull the right edge in.
            //  Without doing this, the size rect for the string is too large.
            var actualRightEdge = firstOffset.X;
            var newFirstGlyphBounds = (firstGlyphIndex > 0)
                ? buffer.Array[buffer.Offset + firstGlyphIndex - 1].EstimateDrawBounds()
                : default(Bounds);
            if (firstGlyphIndex > 0)
                actualRightEdge = Math.Min(
                    actualRightEdge, newFirstGlyphBounds.BottomRight.X
                );

            // FIXME: This will break if the word mixes styles
            baselineAdjustmentStart = firstGlyphIndex;

            if (Markers.Count <= 0)
                return;

            // HACK: If a marker is inside of the wrapped word or around it, we need to adjust the marker to account
            //  for the fact that its anchoring characters have just moved
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                Bounds oldBounds = m.Bounds.LastOrDefault(),
                    newBounds = oldBounds.Translate(adjustment);

                newBounds.TopLeft.X = (position?.X ?? 0) + xOffset;
                newBounds.TopLeft.Y = Math.Max(newBounds.TopLeft.Y, newBounds.BottomRight.Y - currentLineSpacing);

                if ((m.FirstDrawCallIndex == null) || (m.FirstDrawCallIndex > lastGlyphIndex))
                    continue;
                if (m.LastDrawCallIndex < firstGlyphIndex)
                    continue;
                if (m.Bounds.Count < 1)
                    continue;

                m.Bounds[m.Bounds.Count - 1] = newBounds;

                Markers[i] = m;
            }
        }

        private float AdjustCharacterOffsetForBoxes (ref float x, float y1, float h, float? leftPad = null) {
            Bounds b;
            float result = 0;
            var tempBounds = Bounds.FromPositionAndSize(x, y1, 1f, Math.Max(h, 1));
            if ((rowIndex == 0) && (leftPad == null))
                leftPad = xOffsetOfFirstLine;
            for (int i = 0, c = boxes.Count; i < c; i++) {
                boxes.GetItem(i, out b);
                b.BottomRight.X += (leftPad ?? 0f);
                if (!Bounds.Intersect(ref b, ref tempBounds))
                    continue;
                var oldX = x;
                var newX = Math.Max(x, b.BottomRight.X);
                if (!currentLineBreakAtX.HasValue || (newX < currentLineBreakAtX.Value)) {
                    x = newX;
                    result += (oldX - x);
                }
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Snap (ref float x) {
            switch (alignToPixels.Horizontal) {
                case PixelAlignmentMode.Floor:
                    x = (float)Math.Floor(x);
                    break;
                case PixelAlignmentMode.FloorHalf:
                    x = (float)Math.Floor(x * 2) / 2;
                    break;
                case PixelAlignmentMode.FloorQuarter:
                    x = (float)Math.Floor(x * 4) / 4;
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Snap (Vector2 pos, out Vector2 result) {
            switch (alignToPixels.Horizontal) {
                case PixelAlignmentMode.Floor:
                    result.X = (float)Math.Floor(pos.X);
                    break;
                case PixelAlignmentMode.FloorHalf:
                    result.X = (float)Math.Floor(pos.X * 2) / 2;
                    break;
                case PixelAlignmentMode.FloorQuarter:
                    result.X = (float)Math.Floor(pos.X * 4) / 4;
                    break;
                default:
                    result.X = pos.X;
                    break;
            }

            switch (alignToPixels.Vertical) {
                case PixelAlignmentMode.Floor:
                    result.Y = (float)Math.Floor(pos.Y);
                    break;
                case PixelAlignmentMode.FloorHalf:
                    result.Y = (float)Math.Floor(pos.Y * 2) / 2;
                    break;
                case PixelAlignmentMode.FloorQuarter:
                    result.Y = (float)Math.Floor(pos.Y * 4) / 4;
                    break;
                default:
                    result.Y = pos.Y;
                    break;
            }
        }

        private void AlignLine (
            ArraySegment<BitmapDrawCall> buffer, HorizontalAlignment alignment,
            int firstIndex, int lastIndex
        ) {
            var firstDc = buffer.Array[buffer.Offset + firstIndex].EstimateDrawBounds();
            var endDc = buffer.Array[buffer.Offset + lastIndex].EstimateDrawBounds();
            var lineWidth = (endDc.BottomRight.X - firstDc.TopLeft.X);

            // FIXME: Boxes

            float whitespace;
            if (currentLineBreakAtX.HasValue)
                whitespace = currentLineBreakAtX.Value - lineWidth;
            else
                whitespace = maxX - lineWidth;

            // HACK: Don't do anything if the line is too big, just overflow to the right.
            //  Otherwise, the sizing info will be wrong and bad things happen.
            if (whitespace <= 0)
                whitespace = 0;

            // HACK: We compute this before halving the whitespace, so that the size of 
            //  the layout is enough to ensure manually centering the whole layout will
            //  still preserve per-line centering.
            maxX = Math.Max(maxX, whitespace + lineWidth);

            if (alignment == HorizontalAlignment.Center)
                whitespace /= 2;

            Snap(ref whitespace);

            for (var j = firstIndex; j <= lastIndex; j++) {
                if (buffer.Array[buffer.Offset + j].UserData.X > 0)
                    continue;

                buffer.Array[buffer.Offset + j].Position.X += whitespace;
                // We used the sortkey to store line numbers, now we put the right data there
                var key = sortKey;
                if (reverseOrder)
                    key.Order += j;
                buffer.Array[buffer.Offset + j].SortKey = key;
            }
        }

        private void AlignLines (
            ArraySegment<BitmapDrawCall> buffer, HorizontalAlignment alignment
        ) {
            if (buffer.Count == 0)
                return;

            int lineStartIndex = 0;
            float currentLine = buffer.Array[buffer.Offset].SortOrder;

            for (var i = 1; i < buffer.Count; i++) {
                var line = buffer.Array[buffer.Offset + i].SortOrder;

                if (line > currentLine) {
                    AlignLine(buffer, alignment, lineStartIndex, i - 1);

                    lineStartIndex = i;
                    currentLine = line;
                }
            }

            AlignLine(buffer, alignment, lineStartIndex, buffer.Count - 1);
        }

        private void SnapPositions (ArraySegment<BitmapDrawCall> buffer) {
            for (var i = 0; i < buffer.Count; i++)
                Snap(buffer.Array[buffer.Offset + i].Position, out buffer.Array[buffer.Offset + i].Position);
        }

        private void EnsureBufferCapacity (int count) {
            int paddedCount = count + DefaultBufferPadding;

            if (buffer.Array == null) {
                ownsBuffer = true;
                buffer = allocator?.Allocate(paddedCount) ??
                    new ArraySegment<BitmapDrawCall>(new BitmapDrawCall[paddedCount]);
            } else if (buffer.Count < paddedCount) {
                if (ownsBuffer || (allocator != null)) {
                    var oldBuffer = buffer;
                    var newSize = UnorderedList<BitmapDrawCall>.PickGrowthSize(buffer.Count, paddedCount);
                    if (allocator != null)
                        buffer = allocator.Resize(buffer, newSize);
                    else {
                        buffer = new ArraySegment<BitmapDrawCall>(
                            new BitmapDrawCall[newSize]
                        );
                        Array.Copy(oldBuffer.Array, buffer.Array, oldBuffer.Count);
                    }
                } else if (buffer.Count >= count) {
                    // This is OK, there should be enough room...
                    ;
                } else {
                    throw new InvalidOperationException("Buffer too small");
                }
            }
        }

        public bool IsTruncated =>
            // FIXME: < 0 instead of <= 0?
            ((lineLimit ?? int.MaxValue) <= 0) ||
            ((lineBreakLimit ?? int.MaxValue) <= 0) ||
            ((characterLimit ?? int.MaxValue) <= 0);

        public void CreateBox (
            float width, float height, out Bounds box
        ) {
            box = Bounds.FromPositionAndSize(characterOffset.X, characterOffset.Y, width, height);
            CreateBox(ref box);
        }

        public void CreateBox (ref Bounds box) {
            boxes.Add(ref box);
        }

        /// <summary>
        /// Move the character offset forward as if an image of this size had been appended,
        ///  without actually appending anything
        /// </summary>
        public void Advance (
            float width, float height, bool doNotAdjustLineSpacing = false, bool considerBoxes = true
        ) {
            var lineSpacing = height;
            float x = characterOffset.X;
            if (!doNotAdjustLineSpacing)
                ProcessLineSpacingChange(buffer, lineSpacing, lineSpacing);
            var position = new Vector2(characterOffset.X, characterOffset.Y + currentBaseline);
            characterOffset.X += width;
            characterOffsetUnconstrained.X += width;
            if (colIndex == 0) {
                characterOffset.X = Math.Max(characterOffset.X, 0);
                characterOffsetUnconstrained.X = Math.Max(characterOffsetUnconstrained.X, 0);
            }
            if (considerBoxes) {
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, Math.Max(lineSpacing, height));
                AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, Math.Max(lineSpacing, height));
            }
            currentLineMaxX = Math.Max(currentLineMaxX, x);
            currentLineMaxXUnconstrained = Math.Max(currentLineMaxXUnconstrained, x);
            if (characterSkipCount <= 0) {
                characterLimit--;
            } else {
                characterSkipCount--;
            }
        }

        /// <summary>
        /// Append an image as if it were a character
        /// </summary>
        /// <param name="verticalAlignment">Specifies the image's Y origin relative to the baseline</param>
        public void AppendImage (
            Texture2D texture, Bounds? textureRegion = null,
            Vector2? margin = null, 
            float scale = 1, float verticalAlignment = 1,
            Color? multiplyColor = null, bool doNotAdjustLineSpacing = false,
            bool createBox = false, float? hardXAlignment = null, float? hardYAlignment = null,
            float? overrideWidth = null, float? overrideHeight = null
        ) {
            float x = characterOffset.X, y = characterOffset.Y;

            var dc = new BitmapDrawCall {
                Position = Vector2.Zero,
                Texture = texture,
                SortKey = sortKey,
                TextureRegion = textureRegion ?? Bounds.Unit,
                ScaleF = scale * this.scale,
                MultiplyColor = multiplyColor ?? overrideColor ?? Color.White,
                Origin = new Vector2(0, 0),
                // HACK
                UserData = new Vector4(hardXAlignment.HasValue ? 1 : 0, hardYAlignment.HasValue ? 1 : 0, 0, (hardYAlignment.HasValue ? 1 : 1 - verticalAlignment))
            };
            var estimatedBounds = dc.EstimateDrawBounds();
            estimatedBounds.BottomRight.X = estimatedBounds.TopLeft.X + (overrideWidth ?? estimatedBounds.Size.X);
            estimatedBounds.BottomRight.Y = estimatedBounds.TopLeft.Y + (overrideHeight ?? estimatedBounds.Size.Y);
            var lineSpacing = estimatedBounds.Size.Y;
            if (!doNotAdjustLineSpacing)
                ProcessLineSpacingChange(buffer, lineSpacing, lineSpacing);
            float y1 = y,
                y2 = y + currentBaseline - estimatedBounds.Size.Y - (margin?.Y * 0.5f ?? 0);
            float? overrideX = null, overrideY = null;
            if (hardXAlignment.HasValue)
                overrideX = Arithmetic.Lerp(0, (lineBreakAtX ?? 0f) - estimatedBounds.Size.X, hardXAlignment.Value);
            if (hardYAlignment.HasValue)
                overrideY = Arithmetic.Lerp(0, (stopAtY ?? 0f) - estimatedBounds.Size.Y, hardYAlignment.Value);
            if (createBox)
                y2 = Math.Max(y1, y2);

            dc.Position = new Vector2(
                overrideX ?? x, 
                overrideY ?? Arithmetic.Lerp(y1, y2, verticalAlignment)
            );
            estimatedBounds = dc.EstimateDrawBounds();
            estimatedBounds.BottomRight.X = estimatedBounds.TopLeft.X + (overrideWidth ?? estimatedBounds.Size.X);
            estimatedBounds.BottomRight.Y = estimatedBounds.TopLeft.Y + (overrideHeight ?? estimatedBounds.Size.Y);
            var sizeX = (overrideWidth ?? estimatedBounds.Size.X) + (margin?.X ?? 0);
            if (!overrideX.HasValue) {
                characterOffset.X += sizeX;
                characterOffsetUnconstrained.X += sizeX;
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, currentLineSpacing);
                AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, currentLineSpacing);
            }
            dc.Position += actualPosition;
            // FIXME: Margins and stuff
            AppendDrawCall(ref dc, overrideX ?? x, 1, false, currentLineSpacing, 0f, x, ref estimatedBounds, false, false);

            if (createBox) {
                var mx = (margin?.X ?? 0) / 2f;
                var my = (margin?.Y ?? 0) / 2f;
                estimatedBounds.TopLeft.X -= mx;
                estimatedBounds.TopLeft.Y -= my;
                estimatedBounds.BottomRight.X += mx;
                estimatedBounds.BottomRight.Y += my;
                CreateBox(ref estimatedBounds);
            }
        }

        private bool ComputeSuppress (bool? overrideSuppress) {
            if (suppressUntilNextLine)
                return true;
            return overrideSuppress ?? suppress;
        }

        public ArraySegment<BitmapDrawCall> AppendText (
            IGlyphSource font, AbstractString text,
            Dictionary<char, KerningAdjustment> kerningAdjustments = null,
            int? start = null, int? end = null, bool? overrideSuppress = null
        ) {
            if (!IsInitialized)
                throw new InvalidOperationException("Call Initialize first");

            if (font == null)
                throw new ArgumentNullException("font");
            if (text.IsNull)
                throw new ArgumentNullException("text");

            if (!measureOnly)
                EnsureBufferCapacity(bufferWritePosition + text.Length);

            if (kerningAdjustments == null)
                kerningAdjustments = StringLayout.GetDefaultKerningAdjustments(font);

            var effectiveScale = scale / Math.Max(0.0001f, font.DPIScaleFactor);
            var effectiveSpacing = spacing;

            var drawCall = default(BitmapDrawCall);
            drawCall.MultiplyColor = defaultColor;
            drawCall.ScaleF = effectiveScale;
            drawCall.SortKey = sortKey;

            float x = 0;

            for (int i = start ?? 0, l = Math.Min(end ?? text.Length, text.Length); i < l; i++) {
                if (lineLimit.HasValue && lineLimit.Value <= 0)
                    suppress = true;

                char ch1 = text[i],
                    ch2 = i < (l - 1)
                        ? text[i + 1]
                        : '\0';

                int currentCodepointSize = 1;
                uint codepoint;
                if (Unicode.DecodeSurrogatePair(ch1, ch2, out codepoint)) {
                    currentCodepointSize = 2;
                    currentCharacterIndex++;
                    i++;
                } else if (ch1 == '\r') {
                    if (ch2 == '\n') {
                        currentCodepointSize = 2;
                        ch1 = ch2;
                        i++;
                        currentCharacterIndex++;
                    }
                }

                codepoint = replacementCodepoint ?? codepoint;

                bool isWhiteSpace = (char.IsWhiteSpace(ch1) && !replacementCodepoint.HasValue),
                    forcedWrap = false, lineBreak = false,
                    deadGlyph = false, isWordWrapPoint = isWhiteSpace || char.IsSeparator(ch1) ||
                        replacementCodepoint.HasValue || (WordWrapCharacters.IndexOf(codepoint) >= 0),
                    didWrapWord = false;
                Glyph glyph;
                KerningAdjustment kerningAdjustment;

                if (codepoint > 255) {
                    // HACK: Attempt to word-wrap at "other" punctuation in non-western character sets, which will include things like commas
                    // This is less than ideal but .NET does not appear to expose the classification tables needed to do this correctly
                    var category = CharUnicodeInfo.GetUnicodeCategory(ch1);
                    if (category == UnicodeCategory.OtherPunctuation)
                        isWordWrapPoint = true;
                }

                if (ch1 == '\n')
                    lineBreak = true;

                if (lineBreak) {
                    if (lineLimit.HasValue) {
                        lineLimit--;
                        if (lineLimit.Value <= 0)
                            suppress = true;
                    }
                    if (lineBreakLimit.HasValue) {
                        lineBreakLimit--;
                        if (lineBreakLimit.Value <= 0)
                            suppress = true;
                    }
                } else if (lineLimit.HasValue && lineLimit.Value <= 0) {
                    suppress = true;
                }

                if (isWordWrapPoint) {
                    currentLineWrapPointLeft = Math.Max(currentLineWrapPointLeft, characterOffset.X);
                    if (isWhiteSpace)
                        wordStartWritePosition = -1;
                    else
                        wordStartWritePosition = bufferWritePosition;
                    wordStartOffset = characterOffset;
                    wordWrapSuppressed = false;
                } else {
                    if (wordStartWritePosition < 0) {
                        wordStartWritePosition = bufferWritePosition;
                        wordStartOffset = characterOffset;
                    }
                }

                deadGlyph = !font.GetGlyph(codepoint, out glyph);

                float glyphLineSpacing = glyph.LineSpacing * effectiveScale;
                float glyphBaseline = glyph.Baseline * effectiveScale;
                if (deadGlyph) {
                    if (currentLineSpacing > 0) {
                        glyphLineSpacing = currentLineSpacing;
                        glyphBaseline = currentBaseline;
                    } else {
                        Glyph space;
                        if (font.GetGlyph(' ', out space)) {
                            glyphLineSpacing = space.LineSpacing * effectiveScale;
                            glyphBaseline = space.Baseline * effectiveScale;
                        }
                    }
                }

                // glyph.LeftSideBearing *= effectiveSpacing;
                float leftSideDelta = 0;
                if (effectiveSpacing >= 0)
                    glyph.LeftSideBearing *= effectiveSpacing;
                else
                    leftSideDelta = Math.Abs(glyph.LeftSideBearing * effectiveSpacing);
                glyph.RightSideBearing *= effectiveSpacing;
                glyph.RightSideBearing -= leftSideDelta;

                if (initialLineSpacing <= 0)
                    initialLineSpacing = glyphLineSpacing;
                ProcessLineSpacingChange(buffer, glyphLineSpacing, glyphBaseline);

                // FIXME: Don't key kerning adjustments off 'char'
                if ((kerningAdjustments != null) && kerningAdjustments.TryGetValue(ch1, out kerningAdjustment)) {
                    glyph.LeftSideBearing += kerningAdjustment.LeftSideBearing;
                    glyph.Width += kerningAdjustment.Width;
                    glyph.RightSideBearing += kerningAdjustment.RightSideBearing;
                }

                // MonoGame#1355 rears its ugly head: If a character with negative left-side bearing is at the start of a line,
                //  we need to compensate for the bearing to prevent the character from extending outside of the layout bounds
                if (colIndex == 0) {
                    if (glyph.LeftSideBearing < 0)
                        glyph.LeftSideBearing = 0;
                }

                x =
                    characterOffset.X +
                    ((
                        glyph.WidthIncludingBearing + glyph.CharacterSpacing
                    ) * effectiveScale);

                if (x >= currentLineBreakAtX) {
                    if (
                        !deadGlyph &&
                        (colIndex > 0) &&
                        !isWhiteSpace
                    )
                        forcedWrap = true;
                }

                if (forcedWrap) {
                    var currentWordSize = x - wordStartOffset.X;

                    if (
                        wordWrap && !wordWrapSuppressed && 
                        // FIXME: If boxes shrink the current line too far, we want to just keep wrapping until we have enough room
                        //  instead of giving up
                        (currentWordSize <= currentLineBreakAtX)
                    ) {
                        if (lineLimit.HasValue)
                            lineLimit--;
                        WrapWord(buffer, wordStartOffset, wordStartWritePosition, bufferWritePosition - 1, glyphLineSpacing, glyphBaseline, currentWordSize);
                        wordWrapSuppressed = true;
                        lineBreak = true;
                        didWrapWord = true;

                        // FIXME: While this will abort when the line limit is reached, we need to erase the word we wrapped to the next line
                        if (lineLimit.HasValue && lineLimit.Value <= 0)
                            suppress = true;
                    } else if (characterWrap) {
                        if (lineLimit.HasValue)
                            lineLimit--;
                        characterOffset.X = xOffsetOfWrappedLine;
                        AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, currentLineSpacing, leftPad: xOffsetOfWrappedLine);
                        characterOffset.Y += currentLineSpacing;
                        initialLineSpacing = currentLineSpacing = glyphLineSpacing;
                        currentBaseline = glyphBaseline;
                        baselineAdjustmentStart = bufferWritePosition;

                        maxX = Math.Max(maxX, currentLineMaxX);
                        wordStartWritePosition = bufferWritePosition;
                        wordStartOffset = characterOffset;
                        lineBreak = true;

                        if (lineLimit.HasValue && lineLimit.Value <= 0)
                            suppress = true;
                    } else if (hideOverflow) {
                        // If wrapping is disabled but we've hit the line break boundary, we want to suppress glyphs from appearing
                        //  until the beginning of the next line (i.e. hard line break), but continue performing layout
                        suppressUntilNextLine = true;
                    } else {
                        // Just overflow. Hooray!
                    }
                }

                if (lineBreak) {
                    // FIXME: We also want to expand markers to enclose the overhang
                    currentLineMaxX += currentXOverhang;
                    currentLineMaxXUnconstrained += currentXOverhang;

                    if (!forcedWrap) {
                        var spacingForThisLineBreak = currentLineSpacing + extraLineBreakSpacing;
                        if (!suppress) {
                            characterOffset.X = xOffsetOfNewLine;
                            // FIXME: didn't we already do this?
                            characterOffset.Y += spacingForThisLineBreak;
                            maxX = Math.Max(maxX, currentLineMaxX);
                        }
                        characterOffsetUnconstrained.X = xOffsetOfNewLine;
                        AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, spacingForThisLineBreak, leftPad: xOffsetOfNewLine);
                        AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, spacingForThisLineBreak, leftPad: xOffsetOfNewLine);
                        characterOffsetUnconstrained.Y += spacingForThisLineBreak;

                        maxXUnconstrained = Math.Max(maxXUnconstrained, currentLineMaxXUnconstrained);
                        currentLineMaxXUnconstrained = 0;
                        initialLineSpacing = currentLineSpacing = 0;
                        currentBaseline = 0;
                        baselineAdjustmentStart = bufferWritePosition;
                        suppressUntilNextLine = false;
                    }

                    ComputeLineBreakAtX();
                    initialLineXOffset = characterOffset.X;
                    if (!suppress) {
                        currentLineMaxX = 0;
                        currentLineWhitespaceMaxX = 0;
                        currentLineWrapPointLeft = 0;
                    }
                    rowIndex += 1;
                    colIndex = 0;
                }

                // HACK: Recompute after wrapping
                x =
                    characterOffset.X +
                    (glyph.WidthIncludingBearing + glyph.CharacterSpacing) * effectiveScale;
                var yOffset = currentBaseline - glyphBaseline;
                var xUnconstrained = x - characterOffset.X + characterOffsetUnconstrained.X;

                if (deadGlyph || isWhiteSpace) {
                    var whitespaceBounds = Bounds.FromPositionAndSize(
                        new Vector2(characterOffset.X, characterOffset.Y + yOffset),
                        new Vector2(x - characterOffset.X, glyph.LineSpacing * effectiveScale)
                    );
                    // HACK: Why is this necessary?
                    whitespaceBounds.TopLeft.Y = Math.Max(whitespaceBounds.TopLeft.Y, whitespaceBounds.BottomRight.Y - currentLineSpacing);

                    // FIXME: is the center X right?
                    // ProcessHitTests(ref whitespaceBounds, whitespaceBounds.Center.X);
                    // HACK: AppendCharacter will invoke ProcessMarkers anyway
                    // ProcessMarkers(ref whitespaceBounds, currentCodepointSize, null, false, didWrapWord);

                    // Ensure that trailing spaces are factored into total size
                    if (isWhiteSpace)
                        maxX = Math.Max(maxX, whitespaceBounds.BottomRight.X);
                }

                if (deadGlyph) {
                    previousGlyphWasDead = true;
                    currentCharacterIndex++;
                    characterSkipCount--;
                    if (characterLimit.HasValue)
                        characterLimit--;
                    continue;
                }

                if (isWhiteSpace)
                    previousGlyphWasDead = true;
                else
                    previousGlyphWasDead = false;

                if (!ComputeSuppress(overrideSuppress))
                    characterOffset.X += (glyph.CharacterSpacing * effectiveScale);
                characterOffsetUnconstrained.X += (glyph.CharacterSpacing * effectiveScale);
                // FIXME: Is this y/h right
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, glyph.LineSpacing * effectiveScale);
                AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, glyph.LineSpacing * effectiveScale);

                // FIXME: Shift this stuff below into the append function
                var scaledGlyphSize = new Vector2(
                    glyph.WidthIncludingBearing,
                    glyph.LineSpacing
                ) * effectiveScale;

                if (!ComputeSuppress(overrideSuppress))
                    lastCharacterBounds = Bounds.FromPositionAndSize(
                        actualPosition + characterOffset + new Vector2(0, yOffset), scaledGlyphSize
                    );

                var testBounds = lastCharacterBounds;
                var centerX = (characterOffset.X + scaledGlyphSize.X) * 0.5f;
                // FIXME: boxes

                ProcessHitTests(ref testBounds, testBounds.Center.X);

                if ((rowIndex == 0) && (colIndex == 0))
                    firstCharacterBounds = lastCharacterBounds;

                if (!ComputeSuppress(overrideSuppress))
                    characterOffset.X += glyph.LeftSideBearing * effectiveScale;
                characterOffsetUnconstrained.X += glyph.LeftSideBearing * effectiveScale;

                // If a glyph has negative overhang on the right side we want to make a note of that,
                //  so that if a line ends with negative overhang we can expand the layout to include it.
                currentXOverhang = (glyph.RightSideBearing < 0) ? -glyph.RightSideBearing : 0;

                if (!measureOnly && !isWhiteSpace) {
                    var glyphPosition = new Vector2(
                        actualPosition.X + (glyph.XOffset * effectiveScale) + characterOffset.X,
                        actualPosition.Y + (glyph.YOffset * effectiveScale) + characterOffset.Y + yOffset
                    );
                    drawCall.Textures = new TextureSet(glyph.Texture);
                    drawCall.TextureRegion = glyph.BoundsInTexture;
                    drawCall.Position = glyphPosition;
                    drawCall.MultiplyColor = overrideColor ?? glyph.DefaultColor ?? defaultColor;
                }

                AppendDrawCall(
                    ref drawCall, 
                    x, currentCodepointSize, 
                    isWhiteSpace, glyphLineSpacing, 
                    yOffset, xUnconstrained, ref testBounds, 
                    lineBreak, didWrapWord, overrideSuppress
                );

                if (!ComputeSuppress(overrideSuppress))
                    characterOffset.X += (glyph.Width + glyph.RightSideBearing) * effectiveScale;
                characterOffsetUnconstrained.X += (glyph.Width + glyph.RightSideBearing) * effectiveScale;
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, currentLineSpacing);
                AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, currentLineSpacing);
                ProcessLineSpacingChange(buffer, glyphLineSpacing, glyphBaseline);
                maxLineSpacing = Math.Max(maxLineSpacing, currentLineSpacing);

                currentCharacterIndex++;
                colIndex += 1;
            }

            var segment = 
                measureOnly
                    ? default(ArraySegment<BitmapDrawCall>)
                    : new ArraySegment<BitmapDrawCall>(
                        buffer.Array, buffer.Offset, drawCallsWritten
                    );

            maxXUnconstrained = Math.Max(maxXUnconstrained, currentLineMaxXUnconstrained);
            maxX = Math.Max(maxX, currentLineMaxX);

            return segment;
        }

        private void ComputeLineBreakAtX () {
            if (!lineBreakAtX.HasValue) {
                currentLineBreakAtX = null;
                return;
            }
            var row = Bounds.FromPositionAndSize(0f, characterOffset.Y, lineBreakAtX.Value, currentLineSpacing);
            float rightEdge = lineBreakAtX.Value;
            for (int i = 0, c = boxes.Count; i < c; i++) {
                boxes.GetItem(i, out Bounds b);
                // HACK
                if (b.BottomRight.X <= (rightEdge - 2f))
                    continue;
                if (!Bounds.Intersect(ref row, ref b))
                    continue;
                rightEdge = Math.Min(b.TopLeft.X, rightEdge);
            }
            currentLineBreakAtX = rightEdge;
        }

        private void AppendDrawCall (
            ref BitmapDrawCall drawCall, 
            float x, int currentCodepointSize, 
            bool isWhiteSpace, float glyphLineSpacing, float yOffset, 
            float xUnconstrained, ref Bounds testBounds, bool splitMarker, bool didWrapWord,
            bool? overrideSuppress = null
        ) {
            if (recordUsedTextures && (drawCall.Textures.Texture1 != lastUsedTexture) && (drawCall.Textures.Texture1 != null)) {
                lastUsedTexture = drawCall.Textures.Texture1;
                var existingIndex = usedTextures.IndexOf(drawCall.Textures.Texture1);
                if (existingIndex < 0)
                    usedTextures.Add(lastUsedTexture);
            }

            if (colIndex == 0) {
                characterOffset.X = Math.Max(characterOffset.X, 0);
                characterOffsetUnconstrained.X = Math.Max(characterOffsetUnconstrained.X, 0);
            }

            if (stopAtY.HasValue && (characterOffset.Y >= stopAtY))
                suppress = true;

            if (characterSkipCount <= 0) {
                if (characterLimit.HasValue && characterLimit.Value <= 0)
                    suppress = true;

                if (!isWhiteSpace) {
                    if (!measureOnly) {
                        if (bufferWritePosition >= buffer.Count)
                            EnsureBufferCapacity(bufferWritePosition);

                        // HACK so that the alignment pass can detect rows. We strip this later.
                        if (alignment != HorizontalAlignment.Left)
                            drawCall.SortOrder = rowIndex;
                        else if (reverseOrder)
                            drawCall.SortOrder += 1;
                    }

                    if (!ComputeSuppress(overrideSuppress)) {
                        if (!measureOnly) {
                            buffer.Array[buffer.Offset + bufferWritePosition] = drawCall;
                            ProcessMarkers(ref testBounds, currentCodepointSize, bufferWritePosition, splitMarker || previousGlyphWasDead, didWrapWord);
                            bufferWritePosition += 1;
                            drawCallsWritten += 1;
                        }
                        currentLineMaxX = Math.Max(currentLineMaxX, x);
                        maxY = Math.Max(maxY, characterOffset.Y + glyphLineSpacing);
                    } else {
                        drawCallsSuppressed++;
                    }

                    currentLineMaxXUnconstrained = Math.Max(currentLineMaxXUnconstrained, xUnconstrained);
                    maxYUnconstrained = Math.Max(maxYUnconstrained, (characterOffsetUnconstrained.Y + glyphLineSpacing));
                } else {
                    currentLineWrapPointLeft = Math.Max(currentLineWrapPointLeft, characterOffset.X);
                    currentLineWhitespaceMaxX = Math.Max(currentLineWhitespaceMaxX, x);

                    ProcessMarkers(ref testBounds, currentCodepointSize, null, splitMarker || previousGlyphWasDead, didWrapWord);
                }

                characterLimit--;
            } else {
                characterSkipCount--;
            }
        }

        private void Scramble (ArraySegment<BitmapDrawCall> result) {
            var rng = new Random();
            FisherYates.Shuffle(rng, result);
        }

        private void FinishProcessingMarkers (ArraySegment<BitmapDrawCall> result) {
            if (measureOnly)
                return;

            // HACK: During initial layout we split each word of a marked region into
            //  separate bounds so that wrapping would work correctly. Now that we're
            //  done, we want to find words that weren't wrapped and weld their bounds
            //  together so the entire marked string will be one bounds (if possible).
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                if (m.Bounds.Count <= 1)
                    continue;

                for (int j = m.Bounds.Count - 1; j >= 1; j--) {
                    var b1 = m.Bounds[j - 1];
                    var b2 = m.Bounds[j];
                    // HACK: Detect a wrap/line break
                    if (b2.TopLeft.Y >= b1.Center.Y)
                        continue;
                    var xDelta = b2.TopLeft.X - b1.BottomRight.X;
                    if (xDelta > 0.5f)
                        continue;
                    m.Bounds[j - 1] = Bounds.FromUnion(b1, b2);
                    m.Bounds.RemoveAt(j);
                }

                Markers[i] = m;
            }
        }

        public StringLayout Finish () {
            if (currentXOverhang > 0) {
                currentLineMaxX += currentXOverhang;
                currentLineMaxXUnconstrained += currentXOverhang;
                maxX = Math.Max(currentLineMaxX, maxX);
                maxXUnconstrained = Math.Max(currentLineMaxXUnconstrained, maxXUnconstrained);
            }

            var result = default(ArraySegment<BitmapDrawCall>);
            if (!measureOnly) {
                if (buffer.Array != null)
                    result = new ArraySegment<BitmapDrawCall>(
                        buffer.Array, buffer.Offset, drawCallsWritten
                    );

                if (alignment != HorizontalAlignment.Left)
                    AlignLines(result, alignment);
                else
                    SnapPositions(result);

                if (reverseOrder) {
                    for (int k = 0; k < Markers.Count; k++) {
                        var m = Markers[k];
                        var a = result.Count - m.FirstDrawCallIndex - 1;
                        var b = result.Count - m.LastDrawCallIndex - 1;
                        m.FirstDrawCallIndex = b;
                        m.LastDrawCallIndex = a;
                        Markers[k] = m;
                    }

                    int i = result.Offset;
                    int j = result.Offset + result.Count - 1;
                    while (i < j) {
                        var temp = result.Array[i];
                        result.Array[i] = result.Array[j];
                        result.Array[j] = temp;
                        i++;
                        j--;
                    }
                }
            }

            // HACK: For troubleshooting sort issues
            if (false)
                Scramble(result);

            var endpointBounds = lastCharacterBounds;
            // FIXME: Index of last draw call?
            // FIXME: Codepoint size?
            ProcessMarkers(ref endpointBounds, 1, null, false, false);

            FinishProcessingMarkers(result);

            // HACK: Boxes are in local space so we have to offset them at the end
            for (int i = 0, c = boxes.Count; i < c; i++) {
                boxes.GetItem(i, out Bounds box);
                box.TopLeft += actualPosition;
                box.BottomRight += actualPosition;
                boxes[i] = box;
            }

            return new StringLayout(
                position.GetValueOrDefault(), 
                new Vector2(maxX, maxY), new Vector2(maxXUnconstrained, maxYUnconstrained),
                maxLineSpacing,
                firstCharacterBounds, lastCharacterBounds,
                result, (lineLimit.HasValue && (lineLimit.Value <= 0)) || 
                    (lineBreakLimit.HasValue && (lineBreakLimit.Value <= 0))
            ) {
                Boxes = boxes,
                UsedTextures = usedTextures
            };
        }

        public void Dispose () {
        }
    }
}

namespace Squared.Render {
    public static class SpriteFontExtensions {
        public static StringLayout LayoutString (
            this SpriteFont font, AbstractString text, ArraySegment<BitmapDrawCall>? buffer = null,
            Vector2? position = null, Color? color = null, float scale = 1, 
            DrawCallSortKey sortKey = default(DrawCallSortKey),
            int characterSkipCount = 0, int? characterLimit = null,
            float xOffsetOfFirstLine = 0, float? lineBreakAtX = null,
            GlyphPixelAlignment alignToPixels = default(GlyphPixelAlignment),
            Dictionary<char, KerningAdjustment> kerningAdjustments = null,
            bool wordWrap = false, char wrapCharacter = '\0',
            bool reverseOrder = false, HorizontalAlignment? horizontalAlignment = null
        ) {
            var state = new StringLayoutEngine {
                allocator = UnorderedList<BitmapDrawCall>.DefaultAllocator.Instance,
                position = position,
                defaultColor = color ?? Color.White,
                scale = scale,
                sortKey = sortKey,
                characterSkipCount = characterSkipCount,
                characterLimit = characterLimit,
                xOffsetOfFirstLine = xOffsetOfFirstLine,
                lineBreakAtX = lineBreakAtX,
                alignToPixels = alignToPixels,
                characterWrap = lineBreakAtX.HasValue,
                wordWrap = wordWrap,
                wrapCharacter = wrapCharacter,
                buffer = buffer.GetValueOrDefault(default(ArraySegment<BitmapDrawCall>)),
                reverseOrder = reverseOrder
            };
            var gs = new SpriteFontGlyphSource(font);

            if (horizontalAlignment.HasValue)
                state.alignment = horizontalAlignment.Value;

            state.Initialize();

            using (state) {
                var segment = state.AppendText(
                    gs, text, kerningAdjustments
                );

                return state.Finish();
            }
        }

        // Yuck :(
        public static StringLayout LayoutString (
            this IGlyphSource glyphSource, AbstractString text, ArraySegment<BitmapDrawCall>? buffer = null,
            Vector2? position = null, Color? color = null, float scale = 1, 
            DrawCallSortKey sortKey = default(DrawCallSortKey),
            int characterSkipCount = 0, int? characterLimit = null,
            float xOffsetOfFirstLine = 0, float? lineBreakAtX = null,
            bool alignToPixels = false,
            Dictionary<char, KerningAdjustment> kerningAdjustments = null,
            bool wordWrap = false, char wrapCharacter = '\0',
            bool reverseOrder = false, HorizontalAlignment? horizontalAlignment = null
        ) {
            var state = new StringLayoutEngine {
                allocator = UnorderedList<BitmapDrawCall>.DefaultAllocator.Instance,
                position = position,
                defaultColor = color ?? Color.White,
                scale = scale,
                sortKey = sortKey,
                characterSkipCount = characterSkipCount,
                characterLimit = characterLimit,
                xOffsetOfFirstLine = xOffsetOfFirstLine,
                lineBreakAtX = lineBreakAtX,
                alignToPixels = alignToPixels,
                characterWrap = lineBreakAtX.HasValue,
                wordWrap = wordWrap,
                wrapCharacter = wrapCharacter,
                buffer = buffer.GetValueOrDefault(default(ArraySegment<BitmapDrawCall>)),
                reverseOrder = reverseOrder
            };

            if (horizontalAlignment.HasValue)
                state.alignment = horizontalAlignment.Value;

            state.Initialize();

            using (state) {
                var segment = state.AppendText(
                    glyphSource, text, kerningAdjustments
                );

                return state.Finish();
            }
        }
    }

    namespace Text {
        public enum PixelAlignmentMode {
            None = 0,
            Floor = 1,
            // Like Floor but allows half-pixel values (x.5 in addition to x.0)
            FloorHalf = 2,
            FloorQuarter = 4
        }

        public struct GlyphPixelAlignment : IEquatable<GlyphPixelAlignment> {
            public PixelAlignmentMode Horizontal, Vertical;

            public GlyphPixelAlignment (bool alignToPixels) {
                Horizontal = Vertical = alignToPixels ? PixelAlignmentMode.Floor : PixelAlignmentMode.None;
            }

            public GlyphPixelAlignment (PixelAlignmentMode mode) {
                Horizontal = Vertical = mode;
            }

            public GlyphPixelAlignment (PixelAlignmentMode horizontal, PixelAlignmentMode vertical) {
                Horizontal = horizontal;
                Vertical = vertical;
            }

            public static implicit operator GlyphPixelAlignment (bool alignToPixels) {
                return new GlyphPixelAlignment(alignToPixels);
            }

            public static readonly GlyphPixelAlignment Default = new GlyphPixelAlignment(PixelAlignmentMode.None);
            public static readonly GlyphPixelAlignment FloorXY = new GlyphPixelAlignment(PixelAlignmentMode.Floor);
            public static readonly GlyphPixelAlignment FloorY = new GlyphPixelAlignment(PixelAlignmentMode.None, PixelAlignmentMode.Floor);

            public bool Equals (GlyphPixelAlignment other) {
                return (other.Horizontal == Horizontal) && (other.Vertical == Vertical);
            }

            public override bool Equals (object obj) {
                if (obj is GlyphPixelAlignment)
                    return Equals((GlyphPixelAlignment)obj);

                return false;
            }

            public override string ToString () {
                if (Horizontal == Vertical)
                    return Horizontal.ToString();
                else
                    return string.Format("{0}, {1}", Horizontal, Vertical);
            }
        }
    }
}