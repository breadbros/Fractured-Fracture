﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Squared.Game;
using Squared.PRGUI.Decorations;
using Squared.PRGUI.Layout;
using Squared.Render.Convenience;
using Squared.Util;

namespace Squared.PRGUI {
    public abstract class Control {
        public class TabOrderComparer : IComparer<Control> {
            public static readonly TabOrderComparer Instance = new TabOrderComparer();

            public int Compare (Control x, Control y) {
                return x.TabOrder.CompareTo(y.TabOrder);
            }
        }

        public class PaintOrderComparer : IComparer<Control> {
            public static readonly PaintOrderComparer Instance = new PaintOrderComparer();

            public int Compare (Control x, Control y) {
                return x.PaintOrder.CompareTo(y.PaintOrder);
            }
        }

        public IDecorator CustomDecorations, CustomTextDecorations;
        public Margins Margins, Padding;
        public ControlFlags LayoutFlags = ControlFlags.Layout_Fill_Row;
        public float? FixedWidth, FixedHeight;
        public float? MinimumWidth, MinimumHeight;
        public float? MaximumWidth, MaximumHeight;
        public Tween<Color>? BackgroundColor = null;
        public Tween<float> Opacity = 1;
        private bool _BackgroundColorEventFired, _OpacityEventFired;

        // Accumulates scroll offset(s) from parent controls
        private Vector2 _AbsoluteDisplayOffset;

        public ControlStates State;

        internal ControlKey LayoutKey = ControlKey.Invalid;

        public bool Visible { get; set; } = true;
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// Can receive focus via user input
        /// </summary>
        public bool AcceptsFocus { get; protected set; }
        /// <summary>
        /// Receives mouse events and can capture the mouse
        /// </summary>
        public bool AcceptsMouseInput { get; protected set; }
        /// <summary>
        /// Receives keyboard events while focused
        /// </summary>
        public bool AcceptsTextInput { get; protected set; }
        /// <summary>
        /// Intangible controls are ignored by hit-tests
        /// </summary>
        public bool Intangible { get; set; }

        public int TabOrder { get; set; } = 0;
        public int PaintOrder { get; set; } = 0;

        public AbstractTooltipContent TooltipContent = default(AbstractTooltipContent);

        protected virtual bool HasChildren => false;
        protected virtual bool ShouldClipContent => false;

        protected WeakReference<UIContext> WeakContext = null;
        protected WeakReference<Control> WeakParent = null;

        protected float Now => Context?.Now ?? (float)Time.Seconds;
        protected long NowL => Context?.TimeProvider?.Ticks ?? Time.Ticks;

        public UIContext Context {
            get {
                if (WeakContext == null) {
                    if (TryGetParent(out Control parent)) {
                        var result = parent.Context;
                        if (result != null) {
                            SetContext(result);
                            return result;
                        }
                    }
                    return null;
                } else if (WeakContext.TryGetTarget(out UIContext result))
                    return result;
                else
                    return null;
            }
        }

        protected bool FireEvent<T> (string name, T args) {
            return Context?.FireEvent(name, this, args, suppressHandler: true) ?? false;
        }

        protected bool FireEvent (string name) {
            return Context?.FireEvent(name, this, suppressHandler: true) ?? false;
        }

        protected T? AutoFireTweenEvent<T> (long now, string name, ref Tween<T>? tween, ref bool eventFired)
            where T : struct {
            if (!tween.HasValue)
                return null;

            var v = tween.Value;
            return AutoFireTweenEvent(now, name, ref v, ref eventFired);
        }

        protected T AutoFireTweenEvent<T> (long now, string name, ref Tween<T> tween, ref bool eventFired)
            where T : struct {

            if (tween.IsConstant) {
                eventFired = true;
            } else if (tween.IsOver(now)) {
                if (!eventFired) {
                    eventFired = true;
                    FireEvent(name);
                }
            } else {
                eventFired = false;
            }
            return tween.Get(now);
        }

        public Vector2 AbsoluteDisplayOffset {
            get {
                return _AbsoluteDisplayOffset;
            }
            set {
                if (value == _AbsoluteDisplayOffset)
                    return;
                _AbsoluteDisplayOffset = value;
                OnDisplayOffsetChanged();
            }
        }

        protected virtual void OnDisplayOffsetChanged () {
        }

        internal bool HandleEvent (string name) {
            return OnEvent(name);
        }

        internal bool HandleEvent<T> (string name, T args) {
            return OnEvent(name, args);
        }

        protected virtual bool OnEvent (string name) {
            return false;
        }

        protected virtual bool OnEvent<T> (string name, T args) {
            return false;
        }

        internal ControlKey GenerateLayoutTree (UIOperationContext context, ControlKey parent, ControlKey? existingKey = null) {
            return LayoutKey = OnGenerateLayoutTree(context, parent, existingKey);
        }

        protected Vector2 GetFixedInteriorSpace () {
            return new Vector2(
                FixedWidth.HasValue
                    ? Math.Max(0, FixedWidth.Value - Margins.X)
                    : LayoutItem.NoValue,
                FixedHeight.HasValue
                    ? Math.Max(0, FixedHeight.Value - Margins.Y)
                    : LayoutItem.NoValue
            );
        }

        protected virtual bool OnHitTest (LayoutContext context, RectF box, Vector2 position, bool acceptsMouseInputOnly, bool acceptsFocusOnly, ref Control result) {
            if (Intangible)
                return false;
            if (!AcceptsMouseInput && acceptsMouseInputOnly)
                return false;
            if (!AcceptsFocus && acceptsFocusOnly)
                return false;
            if ((acceptsFocusOnly || acceptsMouseInputOnly) && !Enabled)
                return false;

            if (box.Contains(position)) {
                result = this;
                return true;
            }

            return false;
        }

        public RectF GetRect (LayoutContext context, bool includeOffset = true, bool contentRect = false) {
            var result = contentRect 
                ? context.GetContentRect(LayoutKey) 
                : context.GetRect(LayoutKey);
            result.Left += _AbsoluteDisplayOffset.X;
            result.Top += _AbsoluteDisplayOffset.Y;

            // HACK
            if (FixedWidth.HasValue)
                result.Width = FixedWidth.Value;
            if (FixedHeight.HasValue)
                result.Height = FixedHeight.Value;

            if (MinimumWidth.HasValue)
                result.Width = Math.Max(MinimumWidth.Value, result.Width);
            if (MinimumHeight.HasValue)
                result.Height = Math.Max(MinimumHeight.Value, result.Height);
            
            return result;
        }

        protected float GetOpacity (long now) {
            return AutoFireTweenEvent(now, UIEvents.OpacityTweenEnded, ref Opacity, ref _OpacityEventFired);
        }

        protected Color? GetBackgroundColor (long now) {
            return AutoFireTweenEvent(now, UIEvents.BackgroundColorTweenEnded, ref BackgroundColor, ref _BackgroundColorEventFired);
        }

        public Control HitTest (LayoutContext context, Vector2 position, bool acceptsMouseInputOnly, bool acceptsFocusOnly) {
            if (!Visible)
                return null;
            if (LayoutKey.IsInvalid)
                return null;
            if (GetOpacity(Context.TimeProvider.Ticks) <= 0)
                return null;

            var result = this;
            var box = GetRect(context);
            if (OnHitTest(context, box, position, acceptsMouseInputOnly, acceptsFocusOnly, ref result))
                return result;

            return null;
        }

        protected virtual Margins ComputeMargins (UIOperationContext context, IDecorator decorations) {
            var result = Margins;
            if (decorations != null)
                result += decorations.Margins;
            return result;
        }

        protected virtual Margins ComputePadding (UIOperationContext context, IDecorator decorations) {
            var result = Padding;
            if (decorations != null)
                result += decorations.Padding;
            return result;
        }

        protected virtual void ComputeFixedSize (out float? fixedWidth, out float? fixedHeight) {
            fixedWidth = FixedWidth;
            fixedHeight = FixedHeight;
        }

        protected virtual void ComputeSizeConstraints (
            out float? minimumWidth, out float? minimumHeight,
            out float? maximumWidth, out float? maximumHeight
        ) {
            minimumWidth = MinimumWidth;
            minimumHeight = MinimumHeight;
            maximumWidth = MaximumWidth;
            maximumHeight = MaximumHeight;
        }

        protected virtual ControlKey OnGenerateLayoutTree (UIOperationContext context, ControlKey parent, ControlKey? existingKey) {
            var result = existingKey ?? context.Layout.CreateItem();

            var decorations = GetDecorations(context.DecorationProvider);
            var computedMargins = ComputeMargins(context, decorations);
            var computedPadding = ComputePadding(context, decorations);

            ComputeFixedSize(out float? fixedWidth, out float? fixedHeight);
            var actualLayoutFlags = ComputeLayoutFlags(fixedWidth.HasValue, fixedHeight.HasValue);

            context.Layout.SetLayoutFlags(result, actualLayoutFlags);
            context.Layout.SetMargins(result, computedMargins);
            context.Layout.SetPadding(result, computedPadding);
            context.Layout.SetFixedSize(result, fixedWidth ?? LayoutItem.NoValue, fixedHeight ?? LayoutItem.NoValue);

            ComputeSizeConstraints(
                out float? minimumWidth, out float? minimumHeight,
                out float? maximumWidth, out float? maximumHeight
            );
            context.Layout.SetSizeConstraints(
                result, 
                minimumWidth, minimumHeight, 
                maximumWidth, maximumHeight
            );

            if (!parent.IsInvalid && !existingKey.HasValue)
                context.Layout.InsertAtEnd(parent, result);

            return result;
        }

        protected ControlFlags ComputeLayoutFlags (bool hasFixedWidth, bool hasFixedHeight) {
            var result = LayoutFlags;
            // FIXME: If we do this, fixed-size elements extremely are not fixed size
            if (hasFixedWidth && result.IsFlagged(ControlFlags.Layout_Fill_Row))
                result &= ~ControlFlags.Layout_Fill_Row;
            if (hasFixedHeight && result.IsFlagged(ControlFlags.Layout_Fill_Column))
                result &= ~ControlFlags.Layout_Fill_Column;
            return result;
        }

        protected virtual IDecorator GetDefaultDecorations (IDecorationProvider provider) {
            return null;
        }

        protected IDecorator GetDecorations (IDecorationProvider provider) {
            return CustomDecorations ?? GetDefaultDecorations(provider);
        }

        protected IDecorator GetTextDecorations (IDecorationProvider provider) {
            return CustomTextDecorations ?? GetDefaultDecorations(provider);
        }

        protected ControlStates GetCurrentState (UIOperationContext context) {
            var result = State;

            if (!Enabled) {
                result |= ControlStates.Disabled;
            } else {
                if (context.UIContext.Hovering == this)
                    result |= ControlStates.Hovering;
                if (context.UIContext.Focused == this)
                    result |= ControlStates.Focused;
            }

            if (context.UIContext.MouseCaptured == this)
                result |= ControlStates.Pressed;

            return result;
        }

        protected virtual void OnRasterize (UIOperationContext context, ref ImperativeRenderer renderer, DecorationSettings settings, IDecorator decorations) {
            decorations?.Rasterize(context, ref renderer, settings);
        }

        protected virtual void OnRasterizeChildren (UIOperationContext context, ref RasterizePassSet passSet) {
        }

        protected virtual void ApplyClipMargins (UIOperationContext context, ref RectF box) {
        }

        protected virtual DecorationSettings MakeDecorationSettings (ref RectF box, ref RectF contentBox, ControlStates state) {
            return new DecorationSettings {
                Box = box,
                ContentBox = contentBox,
                State = state,
                BackgroundColor = GetBackgroundColor(NowL)
            };
        }

        private void RasterizePass (UIOperationContext context, RectF box, bool compositing, ref RasterizePassSet passSet, ref ImperativeRenderer renderer, RasterizePasses pass) {
            var contentBox = GetRect(context.Layout, contentRect: true);
            var decorations = GetDecorations(context.DecorationProvider);
            var state = GetCurrentState(context);

            var passContext = context.Clone();
            passContext.Pass = pass;
            // passContext.Renderer = context.Renderer.MakeSubgroup();
            var hasNestedContext = (pass == RasterizePasses.Content) && (ShouldClipContent || HasChildren);

            var contentContext = passContext;
            ImperativeRenderer contentRenderer = default(ImperativeRenderer);
            RasterizePassSet childrenPassSet = default(RasterizePassSet);

            // For clipping we need to create a separate batch group that contains all the rasterization work
            //  for our children. At the start of it we'll generate the stencil mask that will be used for our
            //  rendering operation(s).
            if (hasNestedContext) {
                renderer.Layer += 1;
                contentContext = passContext.Clone();
                contentRenderer = renderer.MakeSubgroup();
                if (ShouldClipContent)
                    contentRenderer.DepthStencilState = RenderStates.StencilTest;
                childrenPassSet = new RasterizePassSet(ref passSet.Prepass, ref contentRenderer);
            }

            var settings = MakeDecorationSettings(ref box, ref contentBox, state);
            if (hasNestedContext)
                OnRasterize(contentContext, ref contentRenderer, settings, decorations);
            else
                OnRasterize(contentContext, ref renderer, settings, decorations);

            if ((pass == RasterizePasses.Content) && HasChildren) {
                OnRasterizeChildren(contentContext, ref childrenPassSet);
            }

            if (hasNestedContext) {
                // GROSS OPTIMIZATION HACK: Detect that any rendering operation(s) occurred inside the
                //  group and if so, set up the stencil mask so that they will be clipped.
                if (ShouldClipContent && !contentRenderer.Container.IsEmpty) {
                    contentRenderer.DepthStencilState = RenderStates.StencilWrite;

                    // FIXME: Because we're doing Write here and clearing first, nested clips won't work right.
                    // The solution is probably a combination of test-and-increment when entering the clip,
                    //  and then a test-and-decrement when exiting to restore the previous clip region.
                    contentRenderer.Clear(stencil: 0, layer: -9999);

                    // FIXME: Separate context?
                    contentContext.Pass = RasterizePasses.ContentClip;

                    ApplyClipMargins(contentContext, ref box);

                    contentRenderer.Layer = -999;
                    settings.State = default(ControlStates);
                    decorations.Rasterize(contentContext, ref contentRenderer, settings);
                }

                renderer.Layer += 1;
            }
        }

        private void RasterizeAllPasses (UIOperationContext context, ref RectF box, ref RasterizePassSet passSet, bool compositing) {
            RasterizePass(context, box, compositing, ref passSet, ref passSet.Below, RasterizePasses.Below);
            RasterizePass(context, box, compositing, ref passSet, ref passSet.Content, RasterizePasses.Content);
            RasterizePass(context, box, compositing, ref passSet, ref passSet.Above, RasterizePasses.Above);
        }

        public void Rasterize (UIOperationContext context, ref RasterizePassSet passSet) {
            if (!Visible)
                return;
            if (LayoutKey.IsInvalid)
                return;
            var opacity = GetOpacity(context.UIContext.TimeProvider.Ticks);

            if (opacity <= 0)
                return;

            var box = GetRect(context.Layout);

            if (opacity >= 1) {
                RasterizeAllPasses(context, ref box, ref passSet, false);
            } else {
                // HACK: Create padding around the element for drop shadows
                box.SnapAndInset(out Vector2 tl, out Vector2 br, -16);
                var compositeBox = new RectF(tl, br - tl);
                var rt = context.UIContext.GetScratchRenderTarget(passSet.Prepass.Container.Coordinator, ref compositeBox, out bool needClear);
                try {
                    var compositionContext = context.Clone();

                    // Create nested prepass group before the RT group so that child controls have their prepass operations run before ours
                    var nestedPrepass = passSet.Prepass.MakeSubgroup();
                    var compositionRenderer = passSet.Prepass.ForRenderTarget(rt, name: $"Composite control");
                    compositionRenderer.BlendState = BlendState.AlphaBlend;
                    if (needClear)
                        compositionRenderer.Clear(color: Color.Transparent, stencil: 0, layer: -1);

                    var newPassSet = new RasterizePassSet(ref nestedPrepass, ref compositionRenderer);
                    RasterizeAllPasses(compositionContext, ref box, ref newPassSet, true);
                    compositionRenderer.Layer += 1;
                    var pos = compositeBox.Position.Floor();
                    // FIXME: Is this the right layer?
                    passSet.Above.Draw(
                        rt, position: pos,
                        sourceRectangle: new Rectangle(
                            (int)compositeBox.Left, (int)compositeBox.Top,
                            (int)compositeBox.Width, (int)compositeBox.Height
                        ),
                        blendState: BlendState.AlphaBlend, 
                        multiplyColor: Color.White * opacity
                    );
                    passSet.Above.Layer += 1;
                } finally {
                    context.UIContext.ReleaseScratchRenderTarget(rt);
                }
            }
        }

        public bool TryGetParent (out Control parent) {
            if (WeakParent == null) {
                parent = null;
                return false;
            }

            return WeakParent.TryGetTarget(out parent);
        }

        internal void SetContext (UIContext context) {
            if (WeakContext != null)
                throw new InvalidOperationException("UI context already set");
            // HACK to handle scenarios where a tree of controls are created without a context
            if (context == null)
                return;

            WeakContext = new WeakReference<UIContext>(context, false);
        }

        internal void SetParent (Control parent) {
            LayoutKey = ControlKey.Invalid;

            if (parent == null) {
                WeakParent = null;
                return;
            }

            Control actualParent;
            if ((WeakParent != null) && WeakParent.TryGetTarget(out actualParent)) {
                if (actualParent != parent)
                    throw new Exception("This control already has a parent");
                else
                    return;
            }

            WeakParent = new WeakReference<Control>(parent, false);
            SetContext(parent.Context);
        }

        internal void UnsetParent (Control oldParent) {
            if (WeakParent == null)
                return;

            Control actualParent;
            if (!WeakParent.TryGetTarget(out actualParent))
                return;

            if (actualParent != oldParent)
                throw new Exception("Parent mismatch");

            WeakParent = null;
        }

        public override string ToString () {
            return $"{GetType().Name} #{GetHashCode():X8}";
        }
    }
}
