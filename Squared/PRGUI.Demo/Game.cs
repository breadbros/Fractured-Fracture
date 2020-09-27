﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using Framework;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Squared.Game;
using Squared.PRGUI;
using Squared.PRGUI.Controls;
using Squared.PRGUI.Layout;
using Squared.Render;
using Squared.Render.Convenience;
using Squared.Render.RasterShape;
using Squared.Render.Text;
using Squared.Task;
using Squared.Util;

namespace PRGUI.Demo {
    public class DemoGame : MultithreadedGame {
        public TaskScheduler Scheduler;
        public UIContext Context;

        public GraphicsDeviceManager Graphics;
        public DefaultMaterialSet Materials { get; private set; }

        public EmbeddedTexture2DProvider TextureLoader { get; private set; }
        public EmbeddedFreeTypeFontProvider FontLoader { get; private set; }

        public KeyboardState PreviousKeyboardState, KeyboardState;
        public MouseState PreviousMouseState, MouseState;

        public Material TextMaterial { get; private set; }

        public IGlyphSource Font;
        public AutoRenderTarget UIRenderTarget;

        public const float DPIFactor = 0.5f;

        public bool IsMouseOverUI = false, TearingTest = false;
        public long LastTimeOverUI;

        // public ControlKey MasterList, ContentView;

        public DemoGame () {
            // UniformBinding.ForceCompatibilityMode = true;

            Graphics = new GraphicsDeviceManager(this);
            Graphics.GraphicsProfile = GraphicsProfile.HiDef;
            Graphics.PreferredBackBufferFormat = SurfaceFormat.Color;
            Graphics.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;
            Graphics.PreferredBackBufferWidth = 1920;
            Graphics.PreferredBackBufferHeight = 1080;
            Graphics.SynchronizeWithVerticalRetrace = false;
            Graphics.PreferMultiSampling = false;
            Graphics.IsFullScreen = false;

            Content.RootDirectory = "Content";

            IsFixedTimeStep = false;

            if (IsFixedTimeStep)
                TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);

            PreviousKeyboardState = Keyboard.GetState();

            Scheduler = new TaskScheduler(JobQueue.WindowsMessageBased);
        }

        protected override void Initialize () {
            base.Initialize();
        }

        public bool LeftMouse {
            get {
                return (MouseState.LeftButton == ButtonState.Pressed) && !IsMouseOverUI;
            }
        }

        public bool RightMouse {
            get {
                return (MouseState.RightButton == ButtonState.Pressed) && !IsMouseOverUI;
            }
        }

        private FreeTypeFont LoadFont (string name, float size) {
            var result = FontLoader.Load(name);
            result.SizePoints = size;
            // High-DPI offscreen surface so the text is sharp even at subpixel positions
            result.DPIPercent = (int)(100f / DPIFactor);
            // Big margin on glyphs so shadows aren't clipped
            result.GlyphMargin = 4;
            // Enable mips for soft shadows
            result.MipMapping = true;
            return result;
        }

        protected override void OnLoadContent (bool isReloading) {
            RenderCoordinator.EnableThreading = false;

            TextureLoader = new EmbeddedTexture2DProvider(RenderCoordinator) {
                DefaultOptions = new TextureLoadOptions {
                    Premultiply = true,
                    GenerateMips = true
                }
            };
            FontLoader = new EmbeddedFreeTypeFontProvider(RenderCoordinator);

            float fontScale = 1.2f;
            var firaSans = LoadFont("FiraSans-Medium", 20f * fontScale);
            var jpFallback = LoadFont("NotoSansCJKjp-Regular", 16f * fontScale);

            if (false)
            foreach (var ch in jpFallback.SupportedCodepoints) {
                if (ch <= 0xFFFF)
                    continue;
                Console.WriteLine("\\U00{0:X6}", ch);
            }

            var titleFont = new FreeTypeFont.FontSize(firaSans, 14f);
            var tooltipFont = new FreeTypeFont.FontSize(firaSans, 16f);

            Font = new FallbackGlyphSource(firaSans, jpFallback);

            Materials = new DefaultMaterialSet(RenderCoordinator);

            TextMaterial = Materials.Get(Materials.ScreenSpaceShadowedBitmap, blendState: BlendState.AlphaBlend);
            TextMaterial.Parameters.ShadowColor.SetValue(new Vector4(0, 0, 0, 0.66f));
            TextMaterial.Parameters.ShadowOffset.SetValue(Vector2.One * 1.75f * DPIFactor);
            TextMaterial.Parameters.ShadowMipBias.SetValue(1f);

            var hoveringCtl = new StaticText {
                LayoutFlags = ControlFlags.Layout_Fill,
                AutoSize = false,
                Text = "Hovering: None",
                TooltipContent = "The control the mouse is currently hovering over"
            };

            var lastClickedCtl = new StaticText {
                LayoutFlags = ControlFlags.Layout_Fill,
                AutoSize = false,
                Text = "",
                TooltipContent = "The control most recently clicked with the mouse"
            };

            var testString = "Καλημέρα こんにちは \U0002F8B6\U0002F8CD\U0002F8D3 Hello";

            var textfield = new EditableText {
                Text = testString,
                BackgroundColor = new Color(40, 56, 60),
                // FIXME: This should be at least partially automatic
                MinimumWidth = 400,
                Selection = new Pair<int>(1, testString.Length - 4),
                ScrollOffset = new Vector2(128, 32)
            };

            var hideButton = new Button {
                Text = "Hide",
                AutoSizeWidth = false,
                MaximumWidth = 150,
                Margins = default(Margins),
                LayoutFlags = ControlFlags.Layout_Anchor_Right | ControlFlags.Layout_Fill_Column | ControlFlags.Layout_ForceBreak,
                BackgroundColor = new Color(128, 16, 16),
                TooltipContent = "Hide this window temporarily"
            };

            var windowBgColor = new Color(80, 96, 100);
            var floatingWindow = new Window {
                BackgroundColor = windowBgColor,
                Position = new Vector2(220, 140),
                // MinimumWidth = 400,
                // MinimumHeight = 240,
                Title = "Floating Panel",
                ContainerFlags = ControlFlags.Container_Row | ControlFlags.Container_Align_Start | ControlFlags.Container_Wrap,
                Children = {
                    textfield,
                    hideButton,
                },
                PaintOrder = 1
            };

            var decorations = new Squared.PRGUI.Decorations.DefaultDecorations {
                DefaultFont = Font,
                TitleFont = titleFont,
                TooltipFont = tooltipFont
            };

            var changePaintOrder = new Button {
                MinimumWidth = 400,
                Text = "Change Paint Order",
                BackgroundColor = Color.LightSeaGreen,
                TooltipContent = "This button toggles whether the floating panel is above or below the main container"
            };

            Context = new UIContext(Materials, decorations) {
                Controls = {
                    new Container {
                        BackgroundColor = new Color(48, 48, 48),
                        LayoutFlags = ControlFlags.Layout_Fill,
                        ContainerFlags = ControlFlags.Container_Row | ControlFlags.Container_Align_End | ControlFlags.Container_Wrap | ControlFlags.Container_Constrain_Size,
                        Children = {
                            hoveringCtl,
                            lastClickedCtl,
                            new Button {
                                AutoSizeWidth = false,
                                FixedWidth = 220,
                                Text = "Button 1",
                                TooltipContent = "Hi"
                            },
                            new StaticText {
                                Text = "A Button:",
                                LayoutFlags = ControlFlags.Layout_Fill_Row | ControlFlags.Layout_ForceBreak,
                                TooltipContent = "Nice label.\r\nThis label has a very long tooltip that has embedded line breaks and is just generally long, so that it will get word wrapped and stuff, testing the layout constraints for tooltips"
                            },
                            changePaintOrder,
                            new Button {
                                MinimumWidth = 200,
                                Text = "Disabled Button",
                                Enabled = false,
                                LayoutFlags = ControlFlags.Layout_Fill_Row | ControlFlags.Layout_ForceBreak,
                                BackgroundColor = Color.LightPink
                            },
                            new StaticText {
                                AutoSizeWidth = false,
                                Text = "Static Text 2\r\nLine 2",
                                LayoutFlags = ControlFlags.Layout_Fill_Row | ControlFlags.Layout_ForceBreak,
                                MaximumWidth = 130,
                                Multiline = true,
                                Wrap = false,
                                BackgroundColor = Color.DarkRed
                            },
                            new StaticText {
                                AutoSizeWidth = false,
                                Text = "Static Text 3",
                                TextAlignment = HorizontalAlignment.Right,
                                BackgroundColor = Color.DarkGreen
                            },
                            new StaticText {
                                Text = "Static Text 4",
                                MinimumWidth = 400,
                                BackgroundColor = Color.DarkBlue
                            },
                            new Container {
                                ClipChildren = true,
                                ContainerFlags = ControlFlags.Container_Align_Start | ControlFlags.Container_Row | ControlFlags.Container_Wrap,
                                LayoutFlags = ControlFlags.Layout_Fill | ControlFlags.Layout_ForceBreak,
                                MaximumHeight = 1200,
                                Scrollable = true,
                                ShowHorizontalScrollbar = true,
                                ShowVerticalScrollbar = true,
                                ScrollOffset = new Vector2(0, 22),
                                Children = {
                                    new StaticText {
                                        Text = "Clipped container",
                                        AutoSizeWidth = false,
                                        BackgroundColor = new Color(32, 60, 32),
                                    },
                                    new Button {
                                        Text = "Clipped huge button",
                                        FixedWidth = 1600,
                                        FixedHeight = 1800,
                                        LayoutFlags = ControlFlags.Layout_Fill_Row | ControlFlags.Layout_ForceBreak
                                    }
                                }
                            }
                        }
                    },
                    floatingWindow
                }
            };

            Context.EventBus.Subscribe(null, UIEvents.MouseEnter, (ei) => {
                hoveringCtl.Text = "Hovering: " + ei.Source;
            });

            Context.EventBus.Subscribe(null, UIEvents.Click, (ei) => {
                lastClickedCtl.Text = $"Clicked (#{ei.Arguments}): {ei.Source}";
            });

            Context.EventBus.Subscribe(changePaintOrder, UIEvents.Click, (ei) => {
                floatingWindow.PaintOrder = -floatingWindow.PaintOrder;
            });

            Context.EventBus.Subscribe(hideButton, UIEvents.Click, (ei) => {
                floatingWindow.Intangible = true;
                floatingWindow.Opacity = Tween<float>.StartNow(1, 0, seconds: 1, now: Context.TimeProvider.Ticks);
                Scheduler.Start(new Sleep(2.5))
                    .RegisterOnComplete((_) => {
                        floatingWindow.Opacity = Tween<float>.StartNow(0, 1, seconds: 0.25f, now: Context.TimeProvider.Ticks);
                        floatingWindow.Intangible = false;
                    });
            });

            UIRenderTarget = new AutoRenderTarget(
                RenderCoordinator,
                Graphics.PreferredBackBufferWidth, Graphics.PreferredBackBufferHeight, 
                false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 1
            );

            LastTimeOverUI = Time.Ticks;

            Window.AllowUserResizing = true;
            Window.ClientSizeChanged += Window_ClientSizeChanged;
            Window_ClientSizeChanged(null, EventArgs.Empty);
        }

        private void Window_ClientSizeChanged (object sender, EventArgs e) {
            var pp = GraphicsDevice.PresentationParameters;
            RenderCoordinator.WaitForActiveDraws();
            Materials.ViewTransform = ViewTransform.CreateOrthographic(pp.BackBufferWidth, pp.BackBufferHeight);
            Context.CanvasSize = new Vector2(pp.BackBufferWidth, pp.BackBufferHeight);
            Context.UpdateLayout();
            UIRenderTarget.Resize(pp.BackBufferWidth, pp.BackBufferHeight);
        }

        protected override void OnUnloadContent () {
            Process.GetCurrentProcess().Kill();
            Environment.Exit(0);
        }

        private void SetActiveScene (int index) {
            RenderCoordinator.WaitForActiveDraws();
        }

        private readonly List<double> DrawHistory = new List<double>(),
            WaitHistory = new List<double>();

        protected override void Update (GameTime gameTime) {
            PerformanceStats.Record(this.PreviousFrameTiming);

            PreviousKeyboardState = KeyboardState;
            PreviousMouseState = MouseState;
            KeyboardState = Keyboard.GetState();
            MouseState = Mouse.GetState();

            this.IsMouseVisible = true;

            var mousePosition = new Vector2(MouseState.X, MouseState.Y);

            Context.UpdateLayout();

            Context.UpdateInput(
                mousePosition,
                mouseWheelDelta: (MouseState.ScrollWheelValue - PreviousMouseState.ScrollWheelValue) / 3.2f,
                leftButtonPressed: IsActive && (MouseState.LeftButton == ButtonState.Pressed),
                keyboardState: KeyboardState
            );

            if (Context.MouseOver != null)
                LastTimeOverUI = Time.Ticks;

            if (IsActive) {
                var alt = KeyboardState.IsKeyDown(Keys.LeftAlt) || KeyboardState.IsKeyDown(Keys.RightAlt);
                var wasAlt = PreviousKeyboardState.IsKeyDown(Keys.LeftAlt) || PreviousKeyboardState.IsKeyDown(Keys.RightAlt);

                if (KeyboardState.IsKeyDown(Keys.OemTilde) && !PreviousKeyboardState.IsKeyDown(Keys.OemTilde)) {
                    Graphics.SynchronizeWithVerticalRetrace = !Graphics.SynchronizeWithVerticalRetrace;
                    Graphics.ApplyChangesAfterPresent(RenderCoordinator);
                } else if (
                    (KeyboardState.IsKeyDown(Keys.Enter) && alt) &&
                    (!PreviousKeyboardState.IsKeyDown(Keys.Enter) || !wasAlt)
                ) {
                    Graphics.IsFullScreen = !Graphics.IsFullScreen;
                    Graphics.ApplyChangesAfterPresent(RenderCoordinator);
                } else if (KeyboardState.IsKeyDown(Keys.OemPipe) && !PreviousKeyboardState.IsKeyDown(Keys.OemPipe)) {
                    UniformBinding.ForceCompatibilityMode = !UniformBinding.ForceCompatibilityMode;
                }
            }

            base.Update(gameTime);
        }

        public override void Draw (GameTime gameTime, Frame frame) {
            var pp = GraphicsDevice.PresentationParameters;
            if (
                (pp.BackBufferWidth != UIRenderTarget.Width) ||
                (pp.BackBufferHeight != UIRenderTarget.Height)
            ) {
                Window_ClientSizeChanged(null, EventArgs.Empty);
            }

            Context.Rasterize(frame, UIRenderTarget, -9990, -9991);
            var ir = new ImperativeRenderer(frame, Materials);
            ir.Clear(color: Color.Transparent);
            ir.Layer += 1;

            var hoveringControl = Context.HitTest(new Vector2(MouseState.X, MouseState.Y), false);
            if (hoveringControl != null) {
                var hoveringBox = hoveringControl.GetRect(Context.Layout);

                if (false)
                    ir.RasterizeRectangle(
                        hoveringBox.Position, hoveringBox.Extent,
                        innerColor: new Color(64, 64, 64), outerColor: Color.Black, radius: 4f,
                        fillMode: RasterFillMode.Angular, fillOffset: (float)(Time.Seconds / 6),
                        fillSize: -0.2f, fillAngle: 55,
                        annularRadius: 1.75f, outlineRadius: 0f, outlineColor: Color.Transparent,
                        blendState: BlendState.Additive, blendInLinearSpace: false
                    );
            }

            var elapsedSeconds = TimeSpan.FromTicks(Time.Ticks - LastTimeOverUI).TotalSeconds;
            float uiOpacity = Arithmetic.Lerp(1.0f, 0.66f, (float)((elapsedSeconds - 0.9) * 2.25f));

            ir.Draw(UIRenderTarget, Vector2.Zero, multiplyColor: Color.White * uiOpacity);

            DrawPerformanceStats(ref ir);

            if (TearingTest) {
                var x = (Time.Ticks / 20000) % Graphics.PreferredBackBufferWidth;
                ir.FillRectangle(Bounds.FromPositionAndSize(
                    x, 0, 6, Graphics.PreferredBackBufferHeight
                ), Color.Red);
            }
        }

        private int LastPerformanceStatPrimCount;

        private void DrawPerformanceStats (ref ImperativeRenderer ir) {
            return;

            const float scale = 0.5f;
            var text = PerformanceStats.GetText(-LastPerformanceStatPrimCount);
            text.AppendFormat("{0:000} pass(es)", Context.LastPassCount);
            text.AppendLine();

            using (var buffer = BufferPool<BitmapDrawCall>.Allocate(text.Length)) {
                var layout = Font.LayoutString(text, buffer, scale: scale);
                var layoutSize = layout.Size;
                var position = new Vector2(Graphics.PreferredBackBufferWidth - (240 * scale), Graphics.PreferredBackBufferHeight - (240 * scale)).Floor();
                var dc = layout.DrawCalls;

                // fill quad + text quads
                LastPerformanceStatPrimCount = (layout.Count * 2) + 2;

                ir.RasterizeRectangle(
                    position, position + layoutSize,
                    8, Color.Black * 0.4f, Color.Black * 0.4f
                );
                ir.Layer += 1;
                ir.DrawMultiple(dc, position, material: Materials.ScreenSpaceBitmap, blendState: BlendState.AlphaBlend);
            }
        }
    }
}
