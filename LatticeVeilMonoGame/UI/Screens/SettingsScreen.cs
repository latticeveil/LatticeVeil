using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class OptionsScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;

    private GameSettings _settings;
    private GameSettings _working;
    private string _gpuInfo = "GPU: UNKNOWN";
    private string _displayInfo = "DISPLAY: UNKNOWN";
    private string _profileInfo = "PROFILE: UNKNOWN";
    private int _videoInfoY;
    private static readonly (int w, int h)[] ResolutionCandidates = new[]
    {
        (1280, 720),
        (1920, 1080),
        (2560, 1440),
        (3840, 2160)
    };
    private static readonly (float value, string label)[] GuiScaleCandidates = new[]
    {
        (1.0f, "1.0X"),
        (1.25f, "1.25X"),
        (1.5f, "1.5X"),
        (2.0f, "2.0X")
    };
    private static readonly string[] GuiScaleLabels = GuiScaleCandidates.Select(c => c.label).ToArray();
    private static readonly string[] QualityPresets = { "LOW", "MEDIUM", "HIGH", "ULTRA" };
    private const int ReticleSizeMin = 2;
    private const int ReticleSizeMax = 32;
    private const int ReticleThicknessMin = 1;
    private const int ReticleThicknessMax = 6;
    private static readonly (string value, string label)[] ReticleStyleOptions = new[]
    {
        ("Dot", "DOT"),
        ("Plus", "PLUS"),
        ("Square", "SQUARE"),
        ("Circle", "CIRCLE")
    };
    private static readonly string[] ReticleStyleLabels = ReticleStyleOptions.Select(o => o.label).ToArray();
    private static readonly (string label, string hex, Color color)[] ReticleColorOptions = new[]
    {
        ("WHITE", "FFFFFFC8", new Color(255, 255, 255, 200)),
        ("BLACK", "000000C8", new Color(0, 0, 0, 200)),
        ("GREEN", "8FE38FC8", new Color(143, 227, 143, 200)),
        ("CYAN", "8FD9FFC8", new Color(143, 217, 255, 200)),
        ("YELLOW", "FFE58FC8", new Color(255, 229, 143, 200)),
        ("ORANGE", "FFB36BC8", new Color(255, 179, 107, 200)),
        ("RED", "FF8A8AC8", new Color(255, 138, 138, 200))
    };
    private static readonly string[] ReticleColorLabels = ReticleColorOptions.Select(o => o.label).ToArray();
    private const float BrightnessMin = 0.5f;
    private const float BrightnessMax = 1.5f;
    private const int RenderDistanceMin = 4;
    private const int RenderDistanceMax = 24;
    private const float MouseSensitivityMin = 0.0005f;
    private const float MouseSensitivityMax = 0.01f;
    private const int FovMin = 60;
    private const int FovMax = 110;
    private const int DropdownItemHeight = 34;
    private const int ScrollStep = 40;
    private static readonly RasterizerState ScissorState = new() { ScissorTestEnable = true };
    private const int EnumCurrentSettings = -1;
    private const uint EnumDisplaySettingsRawMode = 0x00000002;
    private const float AspectTolerance = 0.005f;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettingsEx(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    private enum Tab { Video, Audio, Controls, Packs }
    private Tab _tab = Tab.Video;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Button _tabVideo;
    private Button _tabAudio;
    private Button _tabControls;
    private Button _tabPacks;

    private Button _apply;
    private Button _back;
    private Texture2D? _applyDefaultTexture;
    private Texture2D? _applyVideoTexture;
    private Texture2D? _applyAudioTexture;

    // Video controls
    private Checkbox _fullscreen;
    private Checkbox _vsync;
    private Rectangle _guiScaleBox;
    private bool _guiScaleOpen;
    private Rectangle _qualityBox;
    private bool _qualityOpen;
    private Slider _brightness;
    private Slider _fov;
    private Slider _renderDistance;
    private Slider _mouseSensitivity;

    // Resolution dropdown (requested)
    private Rectangle _resolutionBox;
    private bool _resolutionOpen;
    private List<(int w,int h)> _resList = new();
    private int _resolutionListStart;

    // Audio controls
    private Checkbox _multistream;
    private Rectangle _multistreamHelpRect;
    private bool _showMultistreamHelp;
    private Rectangle _inputBox;
    private bool _inputOpen;
    private Rectangle _outputBox;
    private bool _outputOpen;
    private Rectangle _voiceOutputBox;
    private bool _voiceOutputOpen;
    private Rectangle _gameOutputBox;
    private bool _gameOutputOpen;
    private List<DeviceOption> _inputDevices = new();
    private List<DeviceOption> _outputDevices = new();
    private Slider _master;
    private Slider _music;
    private Slider _sfx;
    private Button _micTest;
    private Checkbox _micMonitor;
    private Rectangle _micMeterRect;
    private readonly MicTester _micTester;
    private int _audioRefreshInFlight;
    private AudioDeviceRefreshResult? _pendingAudioRefresh;

    // Controls binding
    private readonly List<string> _bindOrder = new()
    {
        "MoveUp","MoveDown","MoveLeft","MoveRight","Jump","Crouch","Inventory","DropItem","GiveItem","Pause"
    };
    private string? _bindingAction;
    private Rectangle _controlsListRect;
    private Checkbox _reticleEnabled;
    private Rectangle _reticleStyleBox;
    private bool _reticleStyleOpen;
    private Rectangle _reticleColorBox;
    private bool _reticleColorOpen;
    private Rectangle _blockOutlineColorBox;
    private bool _blockOutlineColorOpen;
    private Slider _reticleSize;
    private Slider _reticleThickness;

    // Packs
    private List<string> _availablePacks = new();
    private Rectangle _packsListRect;

    private Point _lastMouse;
    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _contentClipRect;
    private float _scrollVideo;
    private float _scrollAudio;
    private float _scrollControls;
    private float _scrollPacks;

    public OptionsScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _graphics = graphics;
        _micTester = new MicTester(_log);

        _settings = GameSettings.LoadOrCreate(_log);
        _working = GameSettings.LoadOrCreate(_log);
        SnapWorkingVideoOptions();

        _tabVideo = new Button("VIDEO", () => SelectTab(Tab.Video));
        _tabAudio = new Button("AUDIO", () => SelectTab(Tab.Audio));
        _tabControls = new Button("CONTROLS", () => SelectTab(Tab.Controls));
        _tabPacks = new Button("PACKS", () => SelectTab(Tab.Packs));

        _apply = new Button("APPLY", Apply);
        _back = new Button("BACK", ExitOptions);

        _fullscreen = new Checkbox("FULLSCREEN", _working.Fullscreen, v =>
        {
            _working.Fullscreen = v;
            _log.Info($"Option changed: Fullscreen = {v}");
            CaptureVideoInfo();
        });
        _vsync = new Checkbox("VSYNC", _working.VSync, v =>
        {
            _working.VSync = v;
            _log.Info($"Option changed: VSync = {v}");
        });
        _brightness = new Slider("BRIGHTNESS", BrightnessToSlider(_working.Brightness),
            v => _working.Brightness = SliderToBrightness(v),
            v =>
            {
                var value = SliderToBrightness(v);
                _working.Brightness = value;
                _log.Info($"Option changed: Brightness = {value:0.00}");
            });
        _fov = new Slider("FOV", FovToSlider(_working.FieldOfView),
            v => _working.FieldOfView = SliderToFov(v),
            v =>
            {
                var value = SliderToFov(v);
                _working.FieldOfView = value;
                _log.Info($"Option changed: FOV = {value}");
            });
        _renderDistance = new Slider("RENDER DISTANCE", RenderDistanceToSlider(_working.RenderDistanceChunks),
            v => _working.RenderDistanceChunks = SliderToRenderDistance(v),
            v =>
            {
                var value = SliderToRenderDistance(v);
                _working.RenderDistanceChunks = value;
                _log.Info($"Option changed: RenderDistanceChunks = {value}");
            });
        _mouseSensitivity = new Slider("MOUSE SENSITIVITY", SensitivityToSlider(_working.MouseSensitivity),
            v => _working.MouseSensitivity = SliderToSensitivity(v),
            v =>
            {
                var value = SliderToSensitivity(v);
                _working.MouseSensitivity = value;
                _log.Info($"Option changed: MouseSensitivity = {value:0.0000}");
            });
        _reticleEnabled = new Checkbox("RETICLE", _working.ReticleEnabled, v =>
        {
            _working.ReticleEnabled = v;
            _log.Info($"Option changed: ReticleEnabled = {v}");
        });
        _reticleSize = new Slider("RETICLE SIZE", ReticleSizeToSlider(_working.ReticleSize),
            v => _working.ReticleSize = SliderToReticleSize(v),
            v =>
            {
                var value = SliderToReticleSize(v);
                _working.ReticleSize = value;
                _log.Info($"Option changed: ReticleSize = {value}");
            });
        _reticleThickness = new Slider("RETICLE THICKNESS", ReticleThicknessToSlider(_working.ReticleThickness),
            v => _working.ReticleThickness = SliderToReticleThickness(v),
            v =>
            {
                var value = SliderToReticleThickness(v);
                _working.ReticleThickness = value;
                _log.Info($"Option changed: ReticleThickness = {value}");
            });

        _multistream = new Checkbox("MULTISTREAM AUDIO", _working.MultistreamAudio, v =>
        {
            _working.MultistreamAudio = v;
            _log.Info($"Option changed: MultistreamAudio = {v}");
            CloseAllDropdowns();
            _showMultistreamHelp = false;
            UpdateMicMonitor();
            OnResize(_viewport);
        });

        _micTest = new Button("MIC TEST", ToggleMicTest);
        _micMonitor = new Checkbox("MIC MONITOR", false, v =>
        {
            _log.Info($"Option changed: MicMonitor = {v}");
            UpdateMicMonitor();
        });

        _master = new Slider("MASTER", _working.MasterVolume,
            v => _working.MasterVolume = v,
            v => _log.Info($"Option changed: MasterVolume = {(int)(v * 100)}"));
        _music = new Slider("MUSIC", _working.MusicVolume,
            v => _working.MusicVolume = v,
            v => _log.Info($"Option changed: MusicVolume = {(int)(v * 100)}"));
        _sfx = new Slider("SFX", _working.SfxVolume,
            v => _working.SfxVolume = v,
            v => _log.Info($"Option changed: SfxVolume = {(int)(v * 100)}"));

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/Settings.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Settings_GUI.png");

            _tabVideo.Texture = _assets.LoadTexture("textures/menu/buttons/Video.png");
            _tabAudio.Texture = _assets.LoadTexture("textures/menu/buttons/Audio.png");
            _tabControls.Texture = _assets.LoadTexture("textures/menu/buttons/Controls.png");
            _tabPacks.Texture = _assets.LoadTexture("textures/menu/buttons/packs.png");

            _applyDefaultTexture = _assets.LoadTexture("textures/menu/buttons/Apply.png");
            _apply.Texture = _applyDefaultTexture;
            _applyVideoTexture = _applyDefaultTexture;
            _applyAudioTexture = _applyDefaultTexture;
            _back.Texture = _assets.LoadTexture("textures/menu/buttons/OptionsBack.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"OptionsScreen asset load: {ex.Message}");
        }
        UpdateApplyTexture();

        RefreshPacks();
        RefreshAudioDevices();
        CaptureVideoInfo();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(1200, viewport.Width - 20);
        var panelH = Math.Min(760, viewport.Height - 30);
        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);

        // Tabs across the top
        var tabW = panelW / 4 - 10;
        var tabH = (int)(tabW * 0.30f);
        var tabY = _panelRect.Y + 20;
        _tabVideo.Bounds = new Rectangle(_panelRect.X + 10, tabY, tabW, tabH);
        _tabAudio.Bounds = new Rectangle(_tabVideo.Bounds.Right + 10, tabY, tabW, tabH);
        _tabControls.Bounds = new Rectangle(_tabAudio.Bounds.Right + 10, tabY, tabW, tabH);
        _tabPacks.Bounds = new Rectangle(_tabControls.Bounds.Right + 10, tabY, tabW, tabH);

        // Bottom buttons
        var btnW = panelW / 2 - 15;
        var btnH = (int)(btnW * 0.30f);
        var btnY = _panelRect.Bottom - btnH - 20;
        _apply.Bounds = new Rectangle(_panelRect.X + 10, btnY, btnW, btnH);
        _back.Bounds = new Rectangle(_apply.Bounds.Right + 10, btnY, btnW, btnH);

        // Content area
        var contentX = _panelRect.X + 30;
        var contentTop = _tabVideo.Bounds.Bottom + 20;
        var contentY = _tabVideo.Bounds.Bottom + 30;
        var contentW = panelW - 60;
        var contentBottom = _apply.Bounds.Y - 20;
        var contentH = Math.Max(1, contentBottom - contentTop);
        var clipW = Math.Max(1, _panelRect.Width - 40);
        _contentClipRect = new Rectangle(_panelRect.X + 20, contentTop, clipW, contentH);

        var infoHeight = _font.LineHeight + 6;
        _videoInfoY = contentY;
        var videoContentY = contentY + infoHeight;

        var columnGap = 30;
        var columnW = (contentW - columnGap) / 2;
        var leftX = contentX;
        var rightX = contentX + columnW + columnGap;

        _fullscreen.Bounds = new Rectangle(leftX, videoContentY, columnW, 36);
        _vsync.Bounds = new Rectangle(leftX, videoContentY + 50, columnW, 36);

        // Resolution dropdown area
        _resolutionBox = new Rectangle(leftX, videoContentY + 110, Math.Min(420, columnW), 40);

        _guiScaleBox = new Rectangle(rightX, videoContentY, Math.Min(420, columnW), 40);
        _qualityBox = new Rectangle(rightX, videoContentY + 60, Math.Min(420, columnW), 40);
        _brightness.Bounds = new Rectangle(rightX, videoContentY + 120, Math.Min(420, columnW), 18);
        _fov.Bounds = new Rectangle(rightX, videoContentY + 180, Math.Min(420, columnW), 18);
        _renderDistance.Bounds = new Rectangle(rightX, videoContentY + 240, Math.Min(420, columnW), 18);

        // Audio device dropdowns + sliders
        var audioBoxW = contentW;
        _inputBox = new Rectangle(contentX, contentY + 10, audioBoxW, 40);
        _outputBox = new Rectangle(contentX, contentY + 70, audioBoxW, 40);
        _multistream.Bounds = new Rectangle(contentX, contentY + 130, audioBoxW, 36);

        var labelStartX = _multistream.Bounds.X + _multistream.Bounds.Height + 10;
        var labelWidth = (int)_font.MeasureString(_multistream.Label).X;
        var helpX = labelStartX + labelWidth + 10;
        if (helpX + 24 > _panelRect.Right - 10)
            helpX = _panelRect.Right - 34;
        _multistreamHelpRect = new Rectangle(helpX, _multistream.Bounds.Y + 4, 24, 24);

        var audioDetailY = contentY + 190;
        int sliderStartY;
        if (_working.MultistreamAudio)
        {
            _voiceOutputBox = new Rectangle(contentX, audioDetailY, audioBoxW, 40);
            _gameOutputBox = new Rectangle(contentX, audioDetailY + 60, audioBoxW, 40);
            sliderStartY = audioDetailY + 120;
        }
        else
        {
            _voiceOutputBox = Rectangle.Empty;
            _gameOutputBox = Rectangle.Empty;
            sliderStartY = audioDetailY;
        }

        _master.Bounds = new Rectangle(contentX, sliderStartY, Math.Min(520, contentW), 18);
        _music.Bounds = new Rectangle(contentX, sliderStartY + 60, Math.Min(520, contentW), 18);
        _sfx.Bounds = new Rectangle(contentX, sliderStartY + 120, Math.Min(520, contentW), 18);

        var micStartY = sliderStartY + 180;
        var micButtonW = Math.Min(240, contentW);
        _micTest.Bounds = new Rectangle(contentX, micStartY, micButtonW, 40);
        var monitorX = _micTest.Bounds.Right + 20;
        var monitorW = Math.Max(200, contentW - (monitorX - contentX));
        _micMonitor.Bounds = new Rectangle(monitorX, micStartY + 4, monitorW, 36);
        _micMeterRect = new Rectangle(contentX, micStartY + 60, Math.Min(520, contentW), 18);

        _mouseSensitivity.Bounds = new Rectangle(contentX, contentY + 10, Math.Min(520, contentW), 18);
        var reticleTop = contentY + 60;
        _reticleEnabled.Bounds = new Rectangle(contentX, reticleTop, Math.Min(520, contentW), 36);
        _reticleStyleBox = new Rectangle(contentX, reticleTop + 50, Math.Min(520, contentW), 40);
        _reticleColorBox = new Rectangle(contentX, reticleTop + 110, Math.Min(520, contentW), 40);
        _blockOutlineColorBox = new Rectangle(contentX, reticleTop + 170, Math.Min(520, contentW), 40);
        _reticleSize.Bounds = new Rectangle(contentX, reticleTop + 230, Math.Min(520, contentW), 18);
        _reticleThickness.Bounds = new Rectangle(contentX, reticleTop + 290, Math.Min(520, contentW), 18);
        _controlsListRect = new Rectangle(contentX, reticleTop + 350, Math.Min(620, contentW), 300);
        _packsListRect = new Rectangle(contentX, contentY + 20, Math.Min(620, contentW), 300);

        ClampAllScroll();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        if (input.IsNewKeyPress(Keys.Escape))
        {
            ExitOptions();
            return;
        }

        _lastMouse = input.MousePosition;

        ApplyPendingAudioRefresh();

        _tabVideo.Update(input);
        _tabAudio.Update(input);
        _tabControls.Update(input);
        _tabPacks.Update(input);

        _apply.Update(input);
        _back.Update(input);

        HandleScroll(input);

        switch (_tab)
        {
            case Tab.Video:
                UpdateCheckboxWithScroll(_fullscreen, input);
                UpdateCheckboxWithScroll(_vsync, input);
                UpdateResolutionDropdown(input);
                UpdateGuiScaleDropdown(input);
                UpdateQualityDropdown(input);
                UpdateSliderWithScroll(_brightness, input);
                UpdateSliderWithScroll(_fov, input);
                UpdateSliderWithScroll(_renderDistance, input);
                break;

            case Tab.Audio:
                UpdateCheckboxWithScroll(_multistream, input);
                UpdateMultistreamHelp(input);
                UpdateAudioDeviceDropdowns(input);
                UpdateSliderWithScroll(_master, input);
                UpdateSliderWithScroll(_music, input);
                UpdateSliderWithScroll(_sfx, input);
                UpdateMicTest(input);
                break;

            case Tab.Controls:
                UpdateSliderWithScroll(_mouseSensitivity, input);
                UpdateCheckboxWithScroll(_reticleEnabled, input);
                UpdateReticleStyleDropdown(input);
                UpdateReticleColorDropdown(input);
                UpdateBlockOutlineColorDropdown(input);
                UpdateSliderWithScroll(_reticleSize, input);
                UpdateSliderWithScroll(_reticleThickness, input);
                UpdateControlsBinding(input);
                break;

            case Tab.Packs:
                UpdatePacks(input);
                break;
        }
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        sb.Begin(samplerState: SamplerState.PointClamp);

        if (_bg is not null) sb.Draw(_bg, UiLayout.WindowViewport, Color.White);
        else sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0,0,0));

        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        // Panel background
        if (_panel is not null)
            sb.Draw(_panel, _panelRect, Color.White);
        else
            sb.Draw(_pixel, _panelRect, new Color(15,15,15));

        _tabVideo.Draw(sb, _pixel, _font);
        _tabAudio.Draw(sb, _pixel, _font);
        _tabControls.Draw(sb, _pixel, _font);
        _tabPacks.Draw(sb, _pixel, _font);

        _apply.Draw(sb, _pixel, _font);
        _back.Draw(sb, _pixel, _font);

        // Content header
        _font.DrawString(sb, $"OPTIONS: {_tab.ToString().ToUpperInvariant()}", new Vector2(_panelRect.X + 30, _tabVideo.Bounds.Bottom + 6), Color.White);

        sb.End();

        var device = _graphics.GraphicsDevice;
        if (device == null)
            return;

        var priorScissor = device.ScissorRectangle;
        device.ScissorRectangle = UiLayout.ToScreenRect(_contentClipRect);

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform, rasterizerState: ScissorState);
        switch (_tab)
        {
            case Tab.Video:
                DrawVideo(sb);
                break;
            case Tab.Audio:
                DrawAudio(sb);
                break;
            case Tab.Controls:
                DrawControls(sb);
                break;
            case Tab.Packs:
                DrawPacks(sb);
                break;
        }

        sb.End();

        device.ScissorRectangle = priorScissor;
    }

    private void DrawVideo(SpriteBatch sb)
    {
        var scroll = GetScrollOffset();
        var infoX = _fullscreen.Bounds.X;
        var maxInfoWidth = _panelRect.Width - 60;
        _font.DrawString(sb, TrimToWidth(_gpuInfo, maxInfoWidth), new Vector2(infoX, _videoInfoY - scroll), Color.White);

        DrawCheckboxWithScroll(_fullscreen, sb);
        DrawCheckboxWithScroll(_vsync, sb);

        var resolutionBox = ScrollRect(_resolutionBox, scroll);
        DrawResolutionBox(sb, resolutionBox);

        var guiIndex = GetGuiScaleIndex(_working.GuiScale);
        var guiScaleBox = ScrollRect(_guiScaleBox, scroll);
        DrawSimpleDropdownBox(sb, guiScaleBox, "GUI SCALE", _guiScaleOpen, GuiScaleLabels, guiIndex);

        var qualityIndex = GetQualityIndex(_working.QualityPreset);
        var qualityBox = ScrollRect(_qualityBox, scroll);
        DrawSimpleDropdownBox(sb, qualityBox, "QUALITY", _qualityOpen, QualityPresets, qualityIndex);

        DrawSliderWithScroll(_brightness, sb);
        DrawSliderWithScroll(_fov, sb);
        DrawSliderWithScroll(_renderDistance, sb);

        // Draw open lists last so they layer above other controls.
        if (_resolutionOpen)
            DrawResolutionList(sb, resolutionBox);
        if (_guiScaleOpen)
            DrawSimpleDropdownList(sb, guiScaleBox, GuiScaleLabels, guiIndex);
        if (_qualityOpen)
            DrawSimpleDropdownList(sb, qualityBox, QualityPresets, qualityIndex);
    }

    private void UpdateResolutionDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_resolutionBox, scroll);
        if (input.IsNewLeftClick())
        {
            var p = input.MousePosition;

            // toggle open if clicked box
            if (box.Contains(p))
            {
                var wasOpen = _resolutionOpen;
                CloseAllDropdowns();
                _resolutionOpen = !wasOpen;
                if (_resolutionOpen)
                {
                    var visible = GetResolutionVisibleCount(box);
                    EnsureResolutionVisible(visible);
                }
                return;
            }

            if (_resolutionOpen)
            {
                var itemH = DropdownItemHeight;
                var visible = GetResolutionVisibleCount(box);
                var listRect = GetResolutionListRect(box, visible);

                if (listRect.Contains(p))
                {
                    // Determine which item was clicked.
                    var idx = _resolutionListStart + (p.Y - (listRect.Y + 6)) / itemH;
                    if (idx >= 0 && idx < _resList.Count)
                    {
                        var (w,h) = _resList[idx];
                        _working.ResolutionWidth = w;
                        _working.ResolutionHeight = h;
                        _resolutionOpen = false;
                        ResetResolutionListScroll();
                        _log.Info($"Option changed: Resolution = {w}x{h}");
                        return;
                    }
                }
                else
                {
                    // click outside closes dropdown
                    _resolutionOpen = false;
                }
            }
        }
    }

    private void UpdateGuiScaleDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_guiScaleBox, scroll);
        UpdateSimpleDropdown(input, box, ref _guiScaleOpen, GuiScaleLabels, idx =>
        {
            var option = GuiScaleCandidates[idx];
            _working.GuiScale = option.value;
            _log.Info($"Option changed: GuiScale = {option.label}");
        });
    }

    private void UpdateQualityDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_qualityBox, scroll);
        UpdateSimpleDropdown(input, box, ref _qualityOpen, QualityPresets, idx =>
        {
            _working.QualityPreset = QualityPresets[idx];
            _log.Info($"Option changed: QualityPreset = {_working.QualityPreset}");
        });
    }

    private void UpdateReticleStyleDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_reticleStyleBox, scroll);
        UpdateSimpleDropdown(input, box, ref _reticleStyleOpen, ReticleStyleLabels, idx =>
        {
            _working.ReticleStyle = ReticleStyleOptions[idx].value;
            _log.Info($"Option changed: ReticleStyle = {_working.ReticleStyle}");
        });
    }

    private void UpdateReticleColorDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_reticleColorBox, scroll);
        UpdateSimpleDropdown(input, box, ref _reticleColorOpen, ReticleColorLabels, idx =>
        {
            _working.ReticleColor = ReticleColorOptions[idx].hex;
            _log.Info($"Option changed: ReticleColor = {_working.ReticleColor}");
        });
    }

    private void UpdateBlockOutlineColorDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_blockOutlineColorBox, scroll);
        UpdateSimpleDropdown(input, box, ref _blockOutlineColorOpen, ReticleColorLabels, idx =>
        {
            _working.BlockOutlineColor = ReticleColorOptions[idx].hex;
            _log.Info($"Option changed: BlockOutlineColor = {_working.BlockOutlineColor}");
        });
    }

    private void UpdateAudioDeviceDropdowns(InputState input)
    {
        var scroll = GetScrollOffset();
        var inputBox = ScrollRect(_inputBox, scroll);
        var outputBox = ScrollRect(_outputBox, scroll);
        UpdateDeviceDropdown(input, inputBox, ref _inputOpen, _inputDevices, selected =>
        {
            _working.AudioInputDeviceId = selected.Id;
            _log.Info($"Option changed: AudioInputDevice = {selected.Label}");
            RestartMicTestIfRunning();
        });

        UpdateDeviceDropdown(input, outputBox, ref _outputOpen, _outputDevices, selected =>
        {
            _working.AudioOutputDeviceId = selected.Id;
            _log.Info($"Option changed: AudioOutputDevice = {selected.Label}");
            UpdateMicMonitor();
        });

        if (_working.MultistreamAudio)
        {
            var voiceBox = ScrollRect(_voiceOutputBox, scroll);
            var gameBox = ScrollRect(_gameOutputBox, scroll);
            UpdateDeviceDropdown(input, voiceBox, ref _voiceOutputOpen, _outputDevices, selected =>
            {
                _working.VoiceOutputDeviceId = selected.Id;
                _log.Info($"Option changed: VoiceOutputDevice = {selected.Label}");
                UpdateMicMonitor();
            });

            UpdateDeviceDropdown(input, gameBox, ref _gameOutputOpen, _outputDevices, selected =>
            {
                _working.GameOutputDeviceId = selected.Id;
                _log.Info($"Option changed: GameOutputDevice = {selected.Label}");
            });
        }
        else
        {
            _voiceOutputOpen = false;
            _gameOutputOpen = false;
        }
    }

    private void UpdateMultistreamHelp(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var p = input.MousePosition;
        var scroll = GetScrollOffset();
        var helpRect = ScrollRect(_multistreamHelpRect, scroll);
        if (helpRect.Contains(p))
        {
            _showMultistreamHelp = !_showMultistreamHelp;
            return;
        }

        var multiRect = ScrollRect(_multistream.Bounds, scroll);
        if (_showMultistreamHelp && !multiRect.Contains(p))
            _showMultistreamHelp = false;
    }

    private void DrawAudio(SpriteBatch sb)
    {
        var scroll = GetScrollOffset();
        var inputBox = ScrollRect(_inputBox, scroll);
        var outputBox = ScrollRect(_outputBox, scroll);
        DrawDeviceDropdownBox(sb, inputBox, "INPUT DEVICE", _inputOpen, _inputDevices, _working.AudioInputDeviceId);
        DrawDeviceDropdownBox(sb, outputBox, "OUTPUT DEVICE", _outputOpen, _outputDevices, _working.AudioOutputDeviceId);

        DrawCheckboxWithScroll(_multistream, sb);
        var helpRect = ScrollRect(_multistreamHelpRect, scroll);
        DrawHelpIcon(sb, helpRect);
        if (ShouldShowMultistreamHelp(helpRect))
            DrawMultistreamHelp(sb, helpRect);

        if (_working.MultistreamAudio)
        {
            var voiceBox = ScrollRect(_voiceOutputBox, scroll);
            var gameBox = ScrollRect(_gameOutputBox, scroll);
            DrawDeviceDropdownBox(sb, voiceBox, "VOICE CHAT OUTPUT", _voiceOutputOpen, _outputDevices, _working.VoiceOutputDeviceId);
            DrawDeviceDropdownBox(sb, gameBox, "GAME OUTPUT", _gameOutputOpen, _outputDevices, _working.GameOutputDeviceId);
        }

        DrawSliderWithScroll(_master, sb);
        DrawSliderWithScroll(_music, sb);
        DrawSliderWithScroll(_sfx, sb);

        var masterRect = ScrollRect(_master.Bounds, scroll);
        var musicRect = ScrollRect(_music.Bounds, scroll);
        var sfxRect = ScrollRect(_sfx.Bounds, scroll);
        _font.DrawString(sb, $"MASTER: {(int)(_working.MasterVolume * 100)}", new Vector2(masterRect.Right + 20, masterRect.Y - _font.LineHeight + 2), Color.White);
        _font.DrawString(sb, $"MUSIC: {(int)(_working.MusicVolume * 100)}", new Vector2(musicRect.Right + 20, musicRect.Y - _font.LineHeight + 2), Color.White);
        _font.DrawString(sb, $"SFX: {(int)(_working.SfxVolume * 100)}", new Vector2(sfxRect.Right + 20, sfxRect.Y - _font.LineHeight + 2), Color.White);

        UpdateMicTestLabel();
        DrawButtonWithScroll(_micTest, sb);
        DrawCheckboxWithScroll(_micMonitor, sb);
        DrawMicMeter(sb, ScrollRect(_micMeterRect, scroll));

        // Draw open lists last so they layer above other controls.
        if (_inputOpen)
            DrawDeviceDropdownList(sb, inputBox, _inputDevices, _working.AudioInputDeviceId);
        if (_outputOpen)
            DrawDeviceDropdownList(sb, outputBox, _outputDevices, _working.AudioOutputDeviceId);
        if (_working.MultistreamAudio && _voiceOutputOpen)
            DrawDeviceDropdownList(sb, ScrollRect(_voiceOutputBox, scroll), _outputDevices, _working.VoiceOutputDeviceId);
        if (_working.MultistreamAudio && _gameOutputOpen)
            DrawDeviceDropdownList(sb, ScrollRect(_gameOutputBox, scroll), _outputDevices, _working.GameOutputDeviceId);
    }

    private void UpdateMicTest(InputState input)
    {
        UpdateButtonWithScroll(_micTest, input);
        UpdateCheckboxWithScroll(_micMonitor, input);
    }

    private void ToggleMicTest()
    {
        if (_micTester.IsRunning)
        {
            StopMicTest();
            return;
        }

        StartMicTest();
    }

    private void StartMicTest()
    {
        var outputId = GetMonitorOutputDeviceId();
        _micTester.Start(_working.AudioInputDeviceId, outputId, _micMonitor.Value);
    }

    private void StopMicTest()
    {
        if (_micTester.IsRunning)
        {
            _micTester.Stop();
            _log.Info("Mic test stopped.");
        }
    }

    private void RestartMicTestIfRunning()
    {
        if (!_micTester.IsRunning)
            return;

        StartMicTest();
    }

    private void UpdateMicMonitor()
    {
        if (!_micTester.IsRunning)
            return;

        _micTester.SetMonitor(_micMonitor.Value, GetMonitorOutputDeviceId());
    }

    private void UpdateMicTestLabel()
    {
        _micTest.Label = _micTester.IsRunning ? "STOP MIC TEST" : "MIC TEST";
    }

    private void DrawMicMeter(SpriteBatch sb, Rectangle rect)
    {
        _font.DrawString(sb, "MIC LEVEL", new Vector2(rect.X, rect.Y - _font.LineHeight), Color.White);

        sb.Draw(_pixel, rect, new Color(18, 18, 18));
        DrawBorder(sb, rect, Color.White);

        var level = Math.Clamp(_micTester.Level, 0f, 1f);
        var fillWidth = (int)Math.Round((rect.Width - 4) * level);
        if (fillWidth > 0)
        {
            var fill = new Rectangle(rect.X + 2, rect.Y + 2, fillWidth, rect.Height - 4);
            var color = level >= 0.85f ? new Color(220, 60, 60)
                : level >= 0.6f ? new Color(220, 200, 60)
                : new Color(60, 220, 120);
            sb.Draw(_pixel, fill, color);
        }

        var percent = (int)Math.Round(level * 100);
        _font.DrawString(sb, $"{percent}%", new Vector2(rect.Right + 10, rect.Y - 2), Color.White);
    }

    private string? GetMonitorOutputDeviceId()
    {
        if (_working.MultistreamAudio && !string.IsNullOrWhiteSpace(_working.VoiceOutputDeviceId))
            return _working.VoiceOutputDeviceId;

        return _working.AudioOutputDeviceId;
    }

    private void UpdateControlsBinding(InputState input)
    {
        if (_bindingAction is null)
        {
            if (input.IsNewLeftClick())
            {
                var p = input.MousePosition;
                var scroll = GetScrollOffset();
                var listRect = ScrollRect(_controlsListRect, scroll);
                if (listRect.Contains(p))
                {
                    var rowH = 36;
                    var idx = (p.Y - listRect.Y) / rowH;
                    if (idx >= 0 && idx < _bindOrder.Count)
                        _bindingAction = _bindOrder[idx];
                }
            }
        }
        else
        {
            // press a key to bind (ESC cancels)
            if (input.IsNewKeyPress(Keys.Escape))
            {
                _bindingAction = null;
                return;
            }

            foreach (var k in input.GetNewKeys())
            {
                if ((k == Keys.LeftShift || k == Keys.RightShift) && !string.Equals(_bindingAction, "Crouch", StringComparison.Ordinal))
                    continue;
                _working.Keybinds[_bindingAction] = k;
                _log.Info($"Option changed: Keybind {_bindingAction} = {k}");
                _bindingAction = null;
                break;
            }
        }
    }

    private void DrawControls(SpriteBatch sb)
    {
        var scroll = GetScrollOffset();
        DrawSliderWithScroll(_mouseSensitivity, sb);
        var sensRect = ScrollRect(_mouseSensitivity.Bounds, scroll);
        _font.DrawString(sb, $"SENS: {_working.MouseSensitivity:0.0000}", new Vector2(sensRect.Right + 20, sensRect.Y - _font.LineHeight + 2), Color.White);

        DrawCheckboxWithScroll(_reticleEnabled, sb);
        var styleBox = ScrollRect(_reticleStyleBox, scroll);
        var styleIndex = GetReticleStyleIndex(_working.ReticleStyle);
        DrawSimpleDropdownBox(sb, styleBox, "RETICLE STYLE", _reticleStyleOpen, ReticleStyleLabels, styleIndex);

        var colorBox = ScrollRect(_reticleColorBox, scroll);
        var colorIndex = GetReticleColorIndex(_working.ReticleColor);
        DrawColorDropdownBox(sb, colorBox, "RETICLE COLOR", _reticleColorOpen, colorIndex);

        var outlineBox = ScrollRect(_blockOutlineColorBox, scroll);
        var outlineIndex = GetReticleColorIndex(_working.BlockOutlineColor);
        DrawColorDropdownBox(sb, outlineBox, "BLOCK OUTLINE", _blockOutlineColorOpen, outlineIndex);

        DrawSliderWithScroll(_reticleSize, sb);
        DrawSliderWithScroll(_reticleThickness, sb);
        var sizeRect = ScrollRect(_reticleSize.Bounds, scroll);
        _font.DrawString(sb, $"SIZE: {_working.ReticleSize}", new Vector2(sizeRect.Right + 20, sizeRect.Y - _font.LineHeight + 2), Color.White);
        var thickRect = ScrollRect(_reticleThickness.Bounds, scroll);
        _font.DrawString(sb, $"THICK: {_working.ReticleThickness}", new Vector2(thickRect.Right + 20, thickRect.Y - _font.LineHeight + 2), Color.White);

        var listRect = ScrollRect(_controlsListRect, scroll);
        _font.DrawString(sb, "CLICK AN ACTION TO REBIND", new Vector2(listRect.X, listRect.Y - _font.LineHeight), Color.White);

        var rowH = 36;
        for (int i = 0; i < _bindOrder.Count; i++)
        {
            var action = _bindOrder[i];
            var y = listRect.Y + i * rowH;
            var row = new Rectangle(listRect.X, y, listRect.Width, rowH - 4);
            sb.Draw(_pixel, row, new Color(18,18,18));
            DrawBorder(sb, row, Color.White);

            _font.DrawString(sb, action.ToUpperInvariant(), new Vector2(row.X + 10, row.Y + 10), Color.White);

            var key = _working.Keybinds.TryGetValue(action, out var k) ? k.ToString() : "UNBOUND";
            var keyText = _bindingAction == action ? "PRESS KEY..." : key.ToUpperInvariant();
            _font.DrawString(sb, keyText, new Vector2(row.Right - 220, row.Y + 10), Color.White);
        }

        if (_reticleStyleOpen)
            DrawSimpleDropdownList(sb, styleBox, ReticleStyleLabels, styleIndex);
        if (_reticleColorOpen)
            DrawColorDropdownList(sb, colorBox, colorIndex);
        if (_blockOutlineColorOpen)
            DrawColorDropdownList(sb, outlineBox, outlineIndex);
    }

    private void RefreshPacks()
    {
        _availablePacks.Clear();

        try
        {
            var packsDir = Path.Combine(Paths.AssetsDir, "packs");
            if (!Directory.Exists(packsDir))
                packsDir = Path.Combine(Paths.AssetsDir, "Assets", "packs");

            if (Directory.Exists(packsDir))
            {
                foreach (var d in Directory.GetDirectories(packsDir))
                    _availablePacks.Add(Path.GetFileName(d));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"RefreshPacks failed: {ex.Message}");
        }
    }

    private void UpdatePacks(InputState input)
    {
        if (!input.IsNewLeftClick()) return;

        var p = input.MousePosition;
        var scroll = GetScrollOffset();
        var listRect = ScrollRect(_packsListRect, scroll);
        if (!listRect.Contains(p)) return;

        var rowH = 36;
        var idx = (p.Y - listRect.Y) / rowH;
        if (idx < 0 || idx >= _availablePacks.Count) return;

        var pack = _availablePacks[idx];
        if (_working.EnabledPacks.Contains(pack))
        {
            _working.EnabledPacks.Remove(pack);
            _log.Info($"Option changed: Pack disabled = {pack}");
        }
        else
        {
            _working.EnabledPacks.Add(pack);
            _log.Info($"Option changed: Pack enabled = {pack}");
        }
    }

    private void DrawPacks(SpriteBatch sb)
    {
        var scroll = GetScrollOffset();
        var listRect = ScrollRect(_packsListRect, scroll);
        _font.DrawString(sb, "PACKS (Documents/LatticeVeil/Assets/packs)", new Vector2(listRect.X, listRect.Y - _font.LineHeight), Color.White);

        if (_availablePacks.Count == 0)
        {
            _font.DrawString(sb, "NO PACKS FOUND", new Vector2(_packsListRect.X, _packsListRect.Y), Color.White);
            return;
        }

        var rowH = 36;
        for (int i = 0; i < _availablePacks.Count; i++)
        {
            var pack = _availablePacks[i];
            var y = listRect.Y + i * rowH;
            var row = new Rectangle(listRect.X, y, listRect.Width, rowH - 4);
            sb.Draw(_pixel, row, new Color(18,18,18));
            DrawBorder(sb, row, Color.White);

            var enabled = _working.EnabledPacks.Contains(pack);
            _font.DrawString(sb, enabled ? "[X]" : "[ ]", new Vector2(row.X + 10, row.Y + 10), Color.White);
            _font.DrawString(sb, pack.ToUpperInvariant(), new Vector2(row.X + 60, row.Y + 10), Color.White);
        }
    }

    private void UpdateApplyTexture()
    {
        _apply.Texture = _tab switch
        {
            Tab.Video => _applyVideoTexture ?? _applyDefaultTexture ?? _apply.Texture,
            Tab.Audio => _applyAudioTexture ?? _applyDefaultTexture ?? _apply.Texture,
            _ => _applyDefaultTexture ?? _apply.Texture
        };
    }

    private void CloseAllDropdowns()
    {
        _resolutionOpen = false;
        _guiScaleOpen = false;
        _qualityOpen = false;
        _inputOpen = false;
        _outputOpen = false;
        _voiceOutputOpen = false;
        _gameOutputOpen = false;
        _reticleStyleOpen = false;
        _reticleColorOpen = false;
        _blockOutlineColorOpen = false;
    }

    private void UpdateSimpleDropdown(InputState input, Rectangle box, ref bool open, IReadOnlyList<string> items, Action<int> onSelect)
    {
        if (!input.IsNewLeftClick())
            return;

        var p = input.MousePosition;
        if (box.Contains(p))
        {
            var wasOpen = open;
            CloseAllDropdowns();
            open = !wasOpen;
            return;
        }

        if (!open)
            return;

        var listRect = GetDropdownListRect(box, items.Count);
        if (listRect.Contains(p))
        {
            var idx = (p.Y - (listRect.Y + 6)) / DropdownItemHeight;
            if (idx >= 0 && idx < items.Count)
            {
                onSelect(idx);
                open = false;
            }
        }
        else
        {
            open = false;
        }
    }

    private void UpdateDeviceDropdown(InputState input, Rectangle box, ref bool open, List<DeviceOption> options, Action<DeviceOption> onSelect)
    {
        if (box == Rectangle.Empty || !input.IsNewLeftClick())
            return;

        var p = input.MousePosition;
        if (box.Contains(p))
        {
            var wasOpen = open;
            CloseAllDropdowns();
            open = !wasOpen;
            return;
        }

        if (!open)
            return;

        var listRect = GetDropdownListRect(box, options.Count);
        if (listRect.Contains(p))
        {
            var idx = (p.Y - (listRect.Y + 6)) / DropdownItemHeight;
            if (idx >= 0 && idx < options.Count)
            {
                onSelect(options[idx]);
                open = false;
            }
        }
        else
        {
            open = false;
        }
    }

    private void DrawResolutionBox(SpriteBatch sb, Rectangle box)
    {
        _font.DrawString(sb, "RESOLUTION", new Vector2(box.X, box.Y - _font.LineHeight), Color.White);

        sb.Draw(_pixel, box, new Color(20,20,20));
        DrawBorder(sb, box, Color.White);

        var current = $"{_working.ResolutionWidth}x{_working.ResolutionHeight}";
        _font.DrawString(sb, current, new Vector2(box.X + 10, box.Y + 12), Color.White);

        _font.DrawString(sb, _resolutionOpen ? "A" : "V", new Vector2(box.Right - 40, box.Y + 12), Color.White);
    }

    private void DrawResolutionList(SpriteBatch sb, Rectangle box)
    {
        if (!_resolutionOpen || _resList.Count == 0)
            return;

        var itemCount = GetResolutionVisibleCount(box);
        var listRect = GetResolutionListRect(box, itemCount);
        sb.Draw(_pixel, listRect, new Color(12,12,12));
        DrawBorder(sb, listRect, Color.White);

        var y = listRect.Y + 6;
        for (int i = 0; i < itemCount; i++)
        {
            var r = new Rectangle(listRect.X + 6, y, listRect.Width - 12, DropdownItemHeight - 2);
            var idx = _resolutionListStart + i;
            if (idx >= _resList.Count)
                break;

            var (w, h) = _resList[idx];
            var isCurrent = w == _working.ResolutionWidth && h == _working.ResolutionHeight;
            sb.Draw(_pixel, r, isCurrent ? new Color(40,40,40) : new Color(18,18,18));
            _font.DrawString(sb, $"{w}x{h}", new Vector2(r.X + 8, r.Y + 9), Color.White);
            y += DropdownItemHeight;
        }
    }

    private void DrawSimpleDropdownBox(SpriteBatch sb, Rectangle box, string title, bool open, IReadOnlyList<string> items, int currentIndex)
    {
        _font.DrawString(sb, title, new Vector2(box.X, box.Y - _font.LineHeight), Color.White);

        sb.Draw(_pixel, box, new Color(20,20,20));
        DrawBorder(sb, box, Color.White);

        if (items.Count > 0)
        {
            var label = items[Math.Clamp(currentIndex, 0, items.Count - 1)];
            _font.DrawString(sb, TrimToWidth(label, box.Width - 50), new Vector2(box.X + 10, box.Y + 12), Color.White);
        }

        _font.DrawString(sb, open ? "A" : "V", new Vector2(box.Right - 40, box.Y + 12), Color.White);
    }

    private void DrawSimpleDropdownList(SpriteBatch sb, Rectangle box, IReadOnlyList<string> items, int currentIndex)
    {
        if (items.Count == 0)
            return;

        var listRect = GetDropdownListRect(box, items.Count);
        sb.Draw(_pixel, listRect, new Color(12,12,12));
        DrawBorder(sb, listRect, Color.White);

        var y = listRect.Y + 6;
        for (int i = 0; i < items.Count; i++)
        {
            var r = new Rectangle(listRect.X + 6, y, listRect.Width - 12, DropdownItemHeight - 2);
            var isCurrent = i == currentIndex;
            sb.Draw(_pixel, r, isCurrent ? new Color(40,40,40) : new Color(18,18,18));
            _font.DrawString(sb, TrimToWidth(items[i], r.Width - 12), new Vector2(r.X + 8, r.Y + 9), Color.White);
            y += DropdownItemHeight;
        }
    }

    private void DrawColorDropdownBox(SpriteBatch sb, Rectangle box, string title, bool open, int currentIndex)
    {
        _font.DrawString(sb, title, new Vector2(box.X, box.Y - _font.LineHeight), Color.White);

        sb.Draw(_pixel, box, new Color(20,20,20));
        DrawBorder(sb, box, Color.White);

        if (ReticleColorOptions.Length > 0)
        {
            var safeIndex = Math.Clamp(currentIndex, 0, ReticleColorOptions.Length - 1);
            var label = ReticleColorOptions[safeIndex].label;
            _font.DrawString(sb, TrimToWidth(label, box.Width - 80), new Vector2(box.X + 10, box.Y + 12), Color.White);

            var swatchSize = Math.Max(12, box.Height - 16);
            var swatchRect = new Rectangle(box.Right - 80, box.Y + (box.Height - swatchSize) / 2, swatchSize, swatchSize);
            sb.Draw(_pixel, swatchRect, ReticleColorOptions[safeIndex].color);
            DrawBorder(sb, swatchRect, Color.White, 1);
        }

        _font.DrawString(sb, open ? "A" : "V", new Vector2(box.Right - 40, box.Y + 12), Color.White);
    }

    private void DrawColorDropdownList(SpriteBatch sb, Rectangle box, int currentIndex)
    {
        if (ReticleColorOptions.Length == 0)
            return;

        var listRect = GetDropdownListRect(box, ReticleColorOptions.Length);
        sb.Draw(_pixel, listRect, new Color(12,12,12));
        DrawBorder(sb, listRect, Color.White);

        var y = listRect.Y + 6;
        for (int i = 0; i < ReticleColorOptions.Length; i++)
        {
            var r = new Rectangle(listRect.X + 6, y, listRect.Width - 12, DropdownItemHeight - 2);
            var isCurrent = i == currentIndex;
            sb.Draw(_pixel, r, isCurrent ? new Color(40,40,40) : new Color(18,18,18));

            var label = ReticleColorOptions[i].label;
            _font.DrawString(sb, TrimToWidth(label, r.Width - 60), new Vector2(r.X + 8, r.Y + 9), Color.White);

            var swatchSize = Math.Max(10, r.Height - 10);
            var swatchRect = new Rectangle(r.Right - swatchSize - 10, r.Y + (r.Height - swatchSize) / 2, swatchSize, swatchSize);
            sb.Draw(_pixel, swatchRect, ReticleColorOptions[i].color);
            DrawBorder(sb, swatchRect, Color.White, 1);

            y += DropdownItemHeight;
        }
    }

    private void DrawDeviceDropdownBox(SpriteBatch sb, Rectangle box, string title, bool open, List<DeviceOption> options, string selectedId)
    {
        if (box == Rectangle.Empty)
            return;

        _font.DrawString(sb, title, new Vector2(box.X, box.Y - _font.LineHeight), Color.White);

        sb.Draw(_pixel, box, new Color(20,20,20));
        DrawBorder(sb, box, Color.White);

        var currentIndex = GetDeviceIndex(options, selectedId);
        if (options.Count > 0)
        {
            var currentLabel = options[Math.Clamp(currentIndex, 0, options.Count - 1)].Label;
            _font.DrawString(sb, TrimToWidth(currentLabel, box.Width - 50), new Vector2(box.X + 10, box.Y + 12), Color.White);
        }
        else
        {
            _font.DrawString(sb, "DEFAULT", new Vector2(box.X + 10, box.Y + 12), Color.White);
        }

        _font.DrawString(sb, open ? "A" : "V", new Vector2(box.Right - 40, box.Y + 12), Color.White);
    }

    private void DrawDeviceDropdownList(SpriteBatch sb, Rectangle box, List<DeviceOption> options, string selectedId)
    {
        if (box == Rectangle.Empty || options.Count == 0)
            return;

        var currentIndex = GetDeviceIndex(options, selectedId);
        var listRect = GetDropdownListRect(box, options.Count);
        sb.Draw(_pixel, listRect, new Color(12,12,12));
        DrawBorder(sb, listRect, Color.White);

        var y = listRect.Y + 6;
        for (int i = 0; i < options.Count; i++)
        {
            var r = new Rectangle(listRect.X + 6, y, listRect.Width - 12, DropdownItemHeight - 2);
            var isCurrent = i == currentIndex;
            sb.Draw(_pixel, r, isCurrent ? new Color(40,40,40) : new Color(18,18,18));
            _font.DrawString(sb, TrimToWidth(options[i].Label, r.Width - 12), new Vector2(r.X + 8, r.Y + 9), Color.White);
            y += DropdownItemHeight;
        }
    }

    private void DrawHelpIcon(SpriteBatch sb, Rectangle rect)
    {
        if (rect == Rectangle.Empty)
            return;

        sb.Draw(_pixel, rect, new Color(20,20,20));
        DrawBorder(sb, rect, Color.White);

        var label = "?";
        var size = _font.MeasureString(label);
        var pos = new Vector2(rect.Center.X - size.X / 2f, rect.Center.Y - _font.LineHeight / 2f);
        _font.DrawString(sb, label, pos, Color.White);
    }

    private bool ShouldShowMultistreamHelp(Rectangle helpRect) =>
        _showMultistreamHelp || helpRect.Contains(_lastMouse);

    private void DrawMultistreamHelp(SpriteBatch sb, Rectangle helpRect)
    {
        var lines = new[]
        {
            "MULTISTREAM SPLITS GAME AND VOICE AUDIO",
            "TO DIFFERENT OUTPUT DEVICES."
        };

        var width = 0;
        foreach (var line in lines)
            width = Math.Max(width, (int)_font.MeasureString(line).X);

        var rect = new Rectangle(helpRect.Right + 10, helpRect.Y - 6, width + 20, lines.Length * _font.LineHeight + 12);
        if (rect.Right > _panelRect.Right - 10)
            rect.X = _panelRect.Right - rect.Width - 10;
        if (rect.Bottom > _panelRect.Bottom - 10)
            rect.Y = _panelRect.Bottom - rect.Height - 10;

        sb.Draw(_pixel, rect, new Color(12,12,12));
        DrawBorder(sb, rect, Color.White);

        var y = rect.Y + 6;
        for (int i = 0; i < lines.Length; i++)
        {
            _font.DrawString(sb, lines[i], new Vector2(rect.X + 10, y), Color.White);
            y += _font.LineHeight;
        }
    }

    private static Rectangle GetDropdownListRect(Rectangle box, int itemCount) =>
        new(box.X, box.Bottom + 4, box.Width, itemCount * DropdownItemHeight + 6);

    private void HandleScroll(InputState input)
    {
        var delta = input.ScrollDelta;
        if (delta == 0)
            return;

        if (_resolutionOpen)
        {
            var box = ScrollRect(_resolutionBox, GetScrollOffset());
            if (TryHandleResolutionListScroll(input, box))
            {
                _showMultistreamHelp = false;
                return;
            }
        }

        if (!_contentClipRect.Contains(input.MousePosition))
            return;

        var step = Math.Sign(delta) * ScrollStep;
        var scroll = GetScroll(_tab) - step;
        SetScroll(_tab, scroll);
        ClampScroll(_tab);
        _showMultistreamHelp = false;
    }

    private Rectangle ScrollRect(Rectangle rect, float scroll) =>
        new(rect.X, rect.Y - (int)Math.Round(scroll), rect.Width, rect.Height);

    private Rectangle ScrollRect(Rectangle rect, int scroll) =>
        new(rect.X, rect.Y - scroll, rect.Width, rect.Height);

    private Rectangle ScrollRect(Rectangle rect) =>
        ScrollRect(rect, GetScrollOffset());

    private void UpdateCheckboxWithScroll(Checkbox checkbox, InputState input)
    {
        var original = checkbox.Bounds;
        checkbox.Bounds = ScrollRect(original);
        checkbox.Update(input);
        checkbox.Bounds = original;
    }

    private void DrawCheckboxWithScroll(Checkbox checkbox, SpriteBatch sb)
    {
        var original = checkbox.Bounds;
        checkbox.Bounds = ScrollRect(original);
        checkbox.Draw(sb, _pixel, _font);
        checkbox.Bounds = original;
    }

    private void UpdateButtonWithScroll(Button button, InputState input)
    {
        var original = button.Bounds;
        button.Bounds = ScrollRect(original);
        button.Update(input);
        button.Bounds = original;
    }

    private void DrawButtonWithScroll(Button button, SpriteBatch sb)
    {
        var original = button.Bounds;
        button.Bounds = ScrollRect(original);
        button.Draw(sb, _pixel, _font);
        button.Bounds = original;
    }

    private void UpdateSliderWithScroll(Slider slider, InputState input)
    {
        var original = slider.Bounds;
        slider.Bounds = ScrollRect(original);
        slider.Update(input);
        slider.Bounds = original;
    }

    private void DrawSliderWithScroll(Slider slider, SpriteBatch sb)
    {
        var original = slider.Bounds;
        slider.Bounds = ScrollRect(original);
        slider.Draw(sb, _pixel, _font);
        slider.Bounds = original;
    }

    private int GetScrollOffset() => (int)Math.Round(GetScroll(_tab));

    private int GetResolutionVisibleCount(Rectangle box)
    {
        if (_resList.Count == 0)
            return 0;

        var listTop = box.Bottom + 4;
        var maxHeight = _contentClipRect.Bottom - listTop - 6;
        var maxItems = Math.Max(1, maxHeight / DropdownItemHeight);
        return Math.Min(_resList.Count, maxItems);
    }

    private static Rectangle GetResolutionListRect(Rectangle box, int visibleCount) =>
        new(box.X, box.Bottom + 4, box.Width, visibleCount * DropdownItemHeight + 6);

    private void EnsureResolutionVisible(int visibleCount)
    {
        if (_resList.Count == 0)
            return;

        var index = _resList.FindIndex(r => r.w == _working.ResolutionWidth && r.h == _working.ResolutionHeight);
        if (index < 0)
            index = 0;

        if (index < _resolutionListStart)
            _resolutionListStart = index;
        else if (index >= _resolutionListStart + visibleCount)
            _resolutionListStart = index - visibleCount + 1;

        ClampResolutionScroll(visibleCount);
    }

    private void ClampResolutionScroll(int visibleCount)
    {
        var maxStart = Math.Max(0, _resList.Count - visibleCount);
        _resolutionListStart = Math.Clamp(_resolutionListStart, 0, maxStart);
    }

    private bool TryHandleResolutionListScroll(InputState input, Rectangle box)
    {
        var visible = GetResolutionVisibleCount(box);
        if (visible <= 0 || visible >= _resList.Count)
            return false;

        var listRect = GetResolutionListRect(box, visible);
        if (!listRect.Contains(input.MousePosition))
            return false;

        var step = Math.Sign(input.ScrollDelta);
        _resolutionListStart = Math.Clamp(_resolutionListStart - step, 0, _resList.Count - visible);
        return true;
    }

    private void ResetResolutionListScroll()
    {
        _resolutionListStart = 0;
    }

    private float GetScroll(Tab tab) => tab switch
    {
        Tab.Video => _scrollVideo,
        Tab.Audio => _scrollAudio,
        Tab.Controls => _scrollControls,
        Tab.Packs => _scrollPacks,
        _ => 0f
    };

    private void SetScroll(Tab tab, float value)
    {
        switch (tab)
        {
            case Tab.Video: _scrollVideo = value; break;
            case Tab.Audio: _scrollAudio = value; break;
            case Tab.Controls: _scrollControls = value; break;
            case Tab.Packs: _scrollPacks = value; break;
        }
    }

    private void ClampAllScroll()
    {
        ClampScroll(Tab.Video);
        ClampScroll(Tab.Audio);
        ClampScroll(Tab.Controls);
        ClampScroll(Tab.Packs);
    }

    private void ClampScroll(Tab tab)
    {
        var max = GetMaxScroll(tab);
        var value = GetScroll(tab);
        if (value < 0f) value = 0f;
        if (value > max) value = max;
        SetScroll(tab, value);
    }

    private int GetMaxScroll(Tab tab)
    {
        var contentHeight = GetContentHeight(tab);
        if (contentHeight <= 0)
            return 0;

        var max = contentHeight - _contentClipRect.Height;
        return max > 0 ? max : 0;
    }

    private int GetContentHeight(Tab tab)
    {
        var padding = 20;
        var top = _contentClipRect.Y;
        switch (tab)
        {
            case Tab.Video:
            {
                var bottom = Math.Max(_resolutionBox.Bottom, _fov.Bounds.Bottom);
                bottom = Math.Max(bottom, _brightness.Bounds.Bottom);
                bottom = Math.Max(bottom, _renderDistance.Bounds.Bottom);
                bottom = Math.Max(bottom, _qualityBox.Bottom);
                bottom = Math.Max(bottom, _guiScaleBox.Bottom);
                bottom = Math.Max(bottom, _vsync.Bounds.Bottom);
                return Math.Max(0, bottom - top + padding);
            }
            case Tab.Audio:
            {
                var bottom = Math.Max(_sfx.Bounds.Bottom, _music.Bounds.Bottom);
                bottom = Math.Max(bottom, _master.Bounds.Bottom);
                bottom = Math.Max(bottom, _multistream.Bounds.Bottom);
                bottom = Math.Max(bottom, _outputBox.Bottom);
                bottom = Math.Max(bottom, _inputBox.Bottom);
                bottom = Math.Max(bottom, _micTest.Bounds.Bottom);
                bottom = Math.Max(bottom, _micMeterRect.Bottom);
                if (_working.MultistreamAudio)
                {
                    bottom = Math.Max(bottom, _voiceOutputBox.Bottom);
                    bottom = Math.Max(bottom, _gameOutputBox.Bottom);
                }
                return Math.Max(0, bottom - top + padding);
            }
            case Tab.Controls:
            {
                var bottom = Math.Max(_controlsListRect.Bottom, _mouseSensitivity.Bounds.Bottom);
                bottom = Math.Max(bottom, _reticleThickness.Bounds.Bottom);
                return Math.Max(0, bottom - top + padding);
            }
            case Tab.Packs:
                return Math.Max(0, _packsListRect.Bottom - top + padding);
            default:
                return 0;
        }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static float ClampRange(float v, float min, float max) => v < min ? min : (v > max ? max : v);

    private static int GetGuiScaleIndex(float value)
    {
        var bestIndex = 0;
        var bestDelta = Math.Abs(GuiScaleCandidates[0].value - value);
        for (int i = 1; i < GuiScaleCandidates.Length; i++)
        {
            var delta = Math.Abs(GuiScaleCandidates[i].value - value);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static int GetQualityIndex(string? value)
    {
        var normalized = NormalizeQuality(value);
        for (int i = 0; i < QualityPresets.Length; i++)
        {
            if (QualityPresets[i] == normalized)
                return i;
        }
        return 1;
    }

    private static int GetReticleStyleIndex(string? value)
    {
        var normalized = NormalizeReticleStyleValue(value);
        for (int i = 0; i < ReticleStyleOptions.Length; i++)
        {
            if (string.Equals(ReticleStyleOptions[i].value, normalized, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private static int GetReticleColorIndex(string? value)
    {
        var normalized = NormalizeReticleColorValue(value);
        for (int i = 0; i < ReticleColorOptions.Length; i++)
        {
            if (string.Equals(ReticleColorOptions[i].hex, normalized, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private static string NormalizeQuality(string? value)
    {
        var quality = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
        return quality is "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? quality : "MEDIUM";
    }

    private static string NormalizeReticleStyleValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Dot";

        var style = value.Trim();
        if (style.Equals("Dot", StringComparison.OrdinalIgnoreCase)) return "Dot";
        if (style.Equals("Plus", StringComparison.OrdinalIgnoreCase)) return "Plus";
        if (style.Equals("Square", StringComparison.OrdinalIgnoreCase)) return "Square";
        if (style.Equals("Circle", StringComparison.OrdinalIgnoreCase)) return "Circle";
        return "Dot";
    }

    private static string NormalizeReticleColorValue(string? value)
    {
        var fallback = ReticleColorOptions.Length > 0 ? ReticleColorOptions[0].hex : "FFFFFFC8";
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var text = value.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
            text = text.Substring(1);
        if (text.Length == 6)
            text += "FF";
        if (text.Length != 8)
            return fallback;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
                return fallback;
        }

        return text.ToUpperInvariant();
    }

    private static float BrightnessToSlider(float value) =>
        (ClampRange(value, BrightnessMin, BrightnessMax) - BrightnessMin) / (BrightnessMax - BrightnessMin);

    private static float SliderToBrightness(float slider) =>
        BrightnessMin + Clamp01(slider) * (BrightnessMax - BrightnessMin);

    private static float FovToSlider(int value) =>
        (Math.Clamp(value, FovMin, FovMax) - FovMin) / (float)(FovMax - FovMin);

    private static int SliderToFov(float slider) =>
        (int)Math.Round(FovMin + Clamp01(slider) * (FovMax - FovMin));

    private static float RenderDistanceToSlider(int value) =>
        (Math.Clamp(value, RenderDistanceMin, RenderDistanceMax) - RenderDistanceMin) / (float)(RenderDistanceMax - RenderDistanceMin);

    private static int SliderToRenderDistance(float slider) =>
        (int)Math.Round(RenderDistanceMin + Clamp01(slider) * (RenderDistanceMax - RenderDistanceMin));

    private static float SensitivityToSlider(float value) =>
        (ClampRange(value, MouseSensitivityMin, MouseSensitivityMax) - MouseSensitivityMin) / (MouseSensitivityMax - MouseSensitivityMin);

    private static float SliderToSensitivity(float slider) =>
        MouseSensitivityMin + Clamp01(slider) * (MouseSensitivityMax - MouseSensitivityMin);

    private static float ReticleSizeToSlider(int value) =>
        (Math.Clamp(value, ReticleSizeMin, ReticleSizeMax) - ReticleSizeMin) / (float)(ReticleSizeMax - ReticleSizeMin);

    private static int SliderToReticleSize(float slider) =>
        (int)Math.Round(ReticleSizeMin + Clamp01(slider) * (ReticleSizeMax - ReticleSizeMin));

    private static float ReticleThicknessToSlider(int value) =>
        (Math.Clamp(value, ReticleThicknessMin, ReticleThicknessMax) - ReticleThicknessMin) / (float)(ReticleThicknessMax - ReticleThicknessMin);

    private static int SliderToReticleThickness(float slider) =>
        (int)Math.Round(ReticleThicknessMin + Clamp01(slider) * (ReticleThicknessMax - ReticleThicknessMin));

    private void SnapWorkingVideoOptions()
    {
        _working.GuiScale = GuiScaleCandidates[GetGuiScaleIndex(_working.GuiScale)].value;
        _working.QualityPreset = NormalizeQuality(_working.QualityPreset);
        _working.Brightness = ClampRange(_working.Brightness, BrightnessMin, BrightnessMax);
        _working.FieldOfView = Math.Clamp(_working.FieldOfView, FovMin, FovMax);
        _working.RenderDistanceChunks = Math.Clamp(_working.RenderDistanceChunks, RenderDistanceMin, RenderDistanceMax);
    }

    private void RefreshAudioDevices()
    {
        BeginAudioDeviceRefresh();
    }

    private List<DeviceOption> BuildDeviceOptions(AudioDeviceFlow flow)
    {
        var devices = AudioDeviceEnumerator.GetDevices(flow);
        var list = new List<DeviceOption>();

        var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
        var defaultName = defaultDevice != null ? defaultDevice.Name : "SYSTEM DEFAULT";
        list.Add(new DeviceOption("", SanitizeForFont($"DEFAULT: {defaultName}")));

        foreach (var device in devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var label = device.IsDefault ? $"{device.Name} (DEFAULT)" : device.Name;
            list.Add(new DeviceOption(device.Id, SanitizeForFont(label)));
        }

        if (list.Count == 0)
            list.Add(new DeviceOption("", "DEFAULT"));

        return list;
    }

    private void BeginAudioDeviceRefresh()
    {
        if (Interlocked.Exchange(ref _audioRefreshInFlight, 1) == 1)
            return;

        Task.Run(() =>
        {
            try
            {
                var input = BuildDeviceOptions(AudioDeviceFlow.Capture);
                var output = BuildDeviceOptions(AudioDeviceFlow.Render);
                var result = new AudioDeviceRefreshResult(input, output);
                Interlocked.Exchange(ref _pendingAudioRefresh, result);
            }
            catch (Exception ex)
            {
                _log.Warn($"Audio device refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _audioRefreshInFlight, 0);
            }
        });
    }

    private void ApplyPendingAudioRefresh()
    {
        var pending = Interlocked.Exchange(ref _pendingAudioRefresh, null);
        if (pending == null)
            return;

        ApplyDeviceOptions(pending.InputDevices, pending.OutputDevices);
        RestartMicTestIfRunning();
    }

    private void ApplyDeviceOptions(List<DeviceOption> inputDevices, List<DeviceOption> outputDevices)
    {
        _inputDevices = inputDevices;
        _outputDevices = outputDevices;

        if (!ContainsDeviceId(_inputDevices, _working.AudioInputDeviceId))
            _working.AudioInputDeviceId = "";
        if (!ContainsDeviceId(_outputDevices, _working.AudioOutputDeviceId))
            _working.AudioOutputDeviceId = "";
        if (!ContainsDeviceId(_outputDevices, _working.VoiceOutputDeviceId))
            _working.VoiceOutputDeviceId = "";
        if (!ContainsDeviceId(_outputDevices, _working.GameOutputDeviceId))
            _working.GameOutputDeviceId = "";
    }

    private static bool ContainsDeviceId(List<DeviceOption> options, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return true;

        foreach (var option in options)
        {
            if (string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int GetDeviceIndex(List<DeviceOption> options, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return 0;

        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private void Apply()
    {
        try
        {
            _log.Info("Options apply requested.");
            SnapWorkingVideoOptions();
            // Persist
            _working.Save(_log);

            // Copy working -> current
            _settings = GameSettings.LoadOrCreate(_log);

            _working.ApplyGraphics(_graphics);
            CaptureVideoInfo();
            var device = _graphics.GraphicsDevice;
            if (device != null)
            {
                var windowViewport = new Rectangle(0, 0, device.Viewport.Width, device.Viewport.Height);
                UiLayout.Update(windowViewport, UiLayout.GetEffectiveScale(_working.GuiScale));
                _menus.OnResize(UiLayout.Viewport);
            }
            _log.Info("Options applied.");

            // We do apply audio immediately.
            _working.ApplyAudio();
        }
        catch (Exception ex)
        {
            _log.Error($"Apply settings failed: {ex.Message}");
        }
    }

    private void Cancel()
    {
        _log.Info("Options cancel requested.");
        _working = GameSettings.LoadOrCreate(_log);
        SnapWorkingVideoOptions();
        RefreshAudioDevices();
        _fullscreen.Value = _working.Fullscreen;
        _vsync.Value = _working.VSync;
        _brightness.Value = BrightnessToSlider(_working.Brightness);
        _fov.Value = FovToSlider(_working.FieldOfView);
        _renderDistance.Value = RenderDistanceToSlider(_working.RenderDistanceChunks);
        _mouseSensitivity.Value = SensitivityToSlider(_working.MouseSensitivity);
        _reticleEnabled.Value = _working.ReticleEnabled;
        _reticleSize.Value = ReticleSizeToSlider(_working.ReticleSize);
        _reticleThickness.Value = ReticleThicknessToSlider(_working.ReticleThickness);
        _multistream.Value = _working.MultistreamAudio;
        _master.Value = _working.MasterVolume;
        _music.Value = _working.MusicVolume;
        _sfx.Value = _working.SfxVolume;
        _bindingAction = null;
        CloseAllDropdowns();
        _showMultistreamHelp = false;
    }

    private void ExitOptions()
    {
        StopMicTest();
        Apply();
        _menus.Pop();
    }

    private void CaptureVideoInfo()
    {
        try
        {
            var adapter = _graphics.GraphicsDevice?.Adapter ?? GraphicsAdapter.DefaultAdapter;
            var desc = string.IsNullOrWhiteSpace(adapter.Description) ? "UNKNOWN" : adapter.Description;
            _gpuInfo = "GPU: " + SanitizeForFont(desc);

            var mode = adapter.CurrentDisplayMode;
            var deviceName = GetCurrentDisplayDeviceName();
            var desktop = GetDesktopResolution(deviceName, mode.Width, mode.Height);
            var format = SanitizeForFont(mode.Format.ToString());
            _displayInfo = $"DISPLAY: {desktop.w}x{desktop.h} {format}";

            var profile = _graphics.GraphicsDevice?.GraphicsProfile.ToString() ?? "UNKNOWN";
            _profileInfo = "PROFILE: " + SanitizeForFont(profile);

            BuildResolutionList(deviceName, desktop.w, desktop.h);
        }
        catch (Exception ex)
        {
            _log.Warn($"Options GPU info failed: {ex.Message}");
            _gpuInfo = "GPU: UNKNOWN";
            _displayInfo = "DISPLAY: UNKNOWN";
            _profileInfo = "PROFILE: UNKNOWN";
            var fallback = GetDesktopResolution(null, 1280, 720);
            BuildResolutionFallback(fallback.w, fallback.h);
        }
    }

    private void BuildResolutionList(string? deviceName, int desktopW, int desktopH)
    {
        _resList.Clear();

        var supported = GetSupportedResolutions(deviceName);
        var set = new HashSet<(int w, int h)>();

        foreach (var (w, h) in supported)
        {
            if (!IsAspectRatio16x9(w, h))
                continue;
            if (w < 640 || h < 360)
                continue;

            set.Add((w, h));
        }

        if (IsAspectRatio16x9(desktopW, desktopH))
            set.Add((desktopW, desktopH));

        if (set.Count == 0)
        {
            BuildResolutionFallback(desktopW, desktopH);
            return;
        }

        _resList = set.ToList();
        _resList.Sort((a, b) =>
        {
            var cmp = a.w.CompareTo(b.w);
            return cmp != 0 ? cmp : a.h.CompareTo(b.h);
        });

        ResetResolutionListScroll();
        SnapWorkingResolutionToList();
    }

    private void BuildResolutionFallback(int desktopW, int desktopH)
    {
        _resList.Clear();
        foreach (var (w, h) in ResolutionCandidates)
        {
            if (IsAspectRatio16x9(w, h) && w <= desktopW && h <= desktopH)
                _resList.Add((w, h));
        }

        if (_resList.Count == 0)
            _resList.Add((desktopW, desktopH));

        _resList.Sort((a, b) =>
        {
            var cmp = a.w.CompareTo(b.w);
            return cmp != 0 ? cmp : a.h.CompareTo(b.h);
        });

        ResetResolutionListScroll();
        SnapWorkingResolutionToList();
    }

    private string? GetCurrentDisplayDeviceName()
    {
        try
        {
            var handle = _graphics.GraphicsDevice?.PresentationParameters.DeviceWindowHandle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
                return System.Windows.Forms.Screen.FromHandle(handle).DeviceName;
        }
        catch
        {
            // Fall back to primary screen.
        }

        try
        {
            return System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        }
        catch
        {
            return null;
        }
    }

    private (int w, int h) GetDesktopResolution(string? deviceName, int fallbackW, int fallbackH)
    {
        try
        {
            var mode = CreateDevMode();
            if (TryEnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
                return (mode.dmPelsWidth, mode.dmPelsHeight);

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                mode = CreateDevMode();
                if (TryEnumDisplaySettings(null, EnumCurrentSettings, ref mode))
                    return (mode.dmPelsWidth, mode.dmPelsHeight);
            }
        }
        catch
        {
            // Fall back to screen bounds.
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        return (screen.Bounds.Width, screen.Bounds.Height);
                }
            }
        }
        catch
        {
            // Fall back to primary screen.
        }

        try
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            if (primary != null)
                return (primary.Bounds.Width, primary.Bounds.Height);
        }
        catch
        {
            // Fall through to provided fallback.
        }

        return (fallbackW, fallbackH);
    }

    private HashSet<(int w, int h)> GetSupportedResolutions(string? deviceName)
    {
        var supported = new HashSet<(int w, int h)>();

        TryFillSupportedResolutions(supported, deviceName);
        if (supported.Count == 0 && !string.IsNullOrWhiteSpace(deviceName))
            TryFillSupportedResolutions(supported, null);

        try
        {
            var adapter = _graphics.GraphicsDevice?.Adapter ?? GraphicsAdapter.DefaultAdapter;
            var current = adapter.CurrentDisplayMode;
            supported.Add((current.Width, current.Height));
            foreach (var mode in adapter.SupportedDisplayModes)
                supported.Add((mode.Width, mode.Height));
        }
        catch
        {
            // Best-effort: ignore adapter modes if unavailable.
        }

        return supported;
    }

    private static void TryFillSupportedResolutions(HashSet<(int w, int h)> supported, string? deviceName)
    {
        try
        {
            for (var modeNum = 0; ; modeNum++)
            {
                var mode = CreateDevMode();
                if (!TryEnumDisplaySettings(deviceName, modeNum, ref mode))
                    break;

                supported.Add((mode.dmPelsWidth, mode.dmPelsHeight));
            }
        }
        catch
        {
            // Keep supported as-is; fallback will handle.
        }
    }

    private static bool IsAspectRatio16x9(int width, int height)
    {
        var lhs = (long)width * 9;
        var rhs = (long)height * 16;
        var diff = Math.Abs(lhs - rhs);
        var tolerance = Math.Max(1L, (long)Math.Round(rhs * AspectTolerance));
        return diff <= tolerance;
    }

    private static DEVMODE CreateDevMode()
    {
        var mode = new DEVMODE
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (short)Marshal.SizeOf<DEVMODE>()
        };
        return mode;
    }

    private static bool TryEnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE mode)
    {
        if (EnumDisplaySettingsEx(deviceName, modeNum, ref mode, EnumDisplaySettingsRawMode))
            return true;

        return EnumDisplaySettings(deviceName, modeNum, ref mode);
    }

    private void SnapWorkingResolutionToList()
    {
        if (_resList.Count == 0)
            return;

        foreach (var (w, h) in _resList)
        {
            if (w == _working.ResolutionWidth && h == _working.ResolutionHeight)
                return;
        }

        var best = _resList[0];
        foreach (var (w, h) in _resList)
        {
            if (w <= _working.ResolutionWidth && h <= _working.ResolutionHeight)
                best = (w, h);
        }

        _working.ResolutionWidth = best.w;
        _working.ResolutionHeight = best.h;
    }

    private string TrimToWidth(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var charWidth = _font.MeasureString("W").X;
        if (charWidth <= 0)
            return text;

        var maxChars = Math.Max(1, (int)(maxWidth / charWidth));
        if (text.Length <= maxChars)
            return text;

        return text.Substring(0, maxChars);
    }

    private static string SanitizeForFont(string text)
    {
        var upper = text.ToUpperInvariant();
        var sb = new StringBuilder(upper.Length);
        foreach (var c in upper)
        {
            if (c >= 'A' && c <= 'Z') { sb.Append(c); continue; }
            if (c >= '0' && c <= '9') { sb.Append(c); continue; }
            if (c == ' ' || c == '-' || c == '_' || c == '.' || c == ':' || c == '/' || c == '(' || c == ')' || c == '[' || c == ']')
            {
                sb.Append(c);
                continue;
            }
            sb.Append(' ');
        }
        var cleaned = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "UNKNOWN" : cleaned;
    }

    private void SelectTab(Tab tab)
    {
        if (_tab == tab)
            return;

        if (_tab == Tab.Audio && tab != Tab.Audio)
            StopMicTest();

        _tab = tab;
        if (_tab == Tab.Audio)
            RefreshAudioDevices();
        CloseAllDropdowns();
        _showMultistreamHelp = false;
        ClampScroll(tab);
        UpdateApplyTexture();
        _log.Info($"Options tab selected: {tab}");
    }

    private sealed class DeviceOption
    {
        public string Id { get; }
        public string Label { get; }

        public DeviceOption(string id, string label)
        {
            Id = id;
            Label = label;
        }
    }

    private sealed class AudioDeviceRefreshResult
    {
        public List<DeviceOption> InputDevices { get; }
        public List<DeviceOption> OutputDevices { get; }

        public AudioDeviceRefreshResult(List<DeviceOption> inputDevices, List<DeviceOption> outputDevices)
        {
            InputDevices = inputDevices;
            OutputDevices = outputDevices;
        }
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color color, int thickness = 2)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, thickness), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, thickness, r.Height), color);
        sb.Draw(_pixel, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), color);
    }
}



