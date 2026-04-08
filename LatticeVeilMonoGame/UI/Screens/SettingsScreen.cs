using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        (1.0f, "1X"),
        (1.5f, "1.5X"),
        (2.0f, "2X")
    };
    private static readonly string[] GuiScaleLabels = GuiScaleCandidates.Select(c => c.label).ToArray();
    private static readonly string[] QualityPresets = { "LOW", "MEDIUM", "HIGH", "ULTRA" };
    private static readonly string[] ParticlePresets = { "AUTO", "OFF", "LOW", "MEDIUM", "HIGH", "ULTRA" };
    private static readonly (SocialNotificationMode value, string label)[] NotificationModeOptions = new[]
    {
        (SocialNotificationMode.Off, "OFF"),
        (SocialNotificationMode.MessageOnly, "MESSAGE ONLY"),
        (SocialNotificationMode.On, "ON")
    };
    private static readonly string[] NotificationModeLabels = NotificationModeOptions.Select(o => o.label).ToArray();
    private static readonly string[] NametagModeLabels = { "STATIC", "FADE", "OFF" };
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
    private const int RenderDistanceMin = GameSettings.RenderDistanceMin;
    private const int RenderDistanceMax = GameSettings.EngineRenderDistanceMax;
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
    private enum ControlsSubTab { Movement, Gameplay, Interface, AllBinds }
    private Tab _tab = Tab.Video;
    private ControlsSubTab _controlsSubTab = ControlsSubTab.Movement;

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

    // Tab selected textures
    private Texture2D? _videoSelectedTex;
    private Texture2D? _audioSelectedTex;
    private Texture2D? _controlsSelectedTex;
    private Texture2D? _packsSelectedTex;

    // Video controls
    private Checkbox _fullscreen;
    private Checkbox _vsync;
    private Rectangle _guiScaleBox;
    private bool _guiScaleOpen;
    private Rectangle _qualityBox;
    private bool _qualityOpen;
    private Rectangle _particleBox;
    private bool _particleOpen;
    private Rectangle _notificationModeBox;
    private bool _notificationModeOpen;
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
        "MoveUp","MoveDown","MoveLeft","MoveRight","Jump","Crouch","Sprint","FlyDescend","Inventory","DropItem","GiveItem","Pause","Chat","Command","HomeGui","StructureFinder","GamemodeModifier","GamemodeWheel","VeilseerXrayToggle","InviteQuickAction"
    };
    private static readonly string[] MovementBindActions =
    {
        "MoveUp", "MoveDown", "MoveLeft", "MoveRight", "Jump", "Crouch", "Sprint", "FlyDescend"
    };
    private static readonly string[] GameplayBindActions =
    {
        "Inventory", "DropItem", "GiveItem", "HomeGui", "StructureFinder", "GamemodeModifier", "GamemodeWheel", "VeilseerXrayToggle", "InviteQuickAction"
    };
    private static readonly string[] InterfaceBindActions =
    {
        "Pause", "Chat", "Command"
    };
    private string? _bindingAction;
    private Rectangle _controlsListRect;
    private Rectangle _controlsMovementTabRect;
    private Rectangle _controlsGameplayTabRect;
    private Rectangle _controlsInterfaceTabRect;
    private Rectangle _controlsAllBindsTabRect;
    private Rectangle _controlsBodyClipRect;
    private Rectangle _crouchModeCycleRect;
    private Rectangle _sprintModeCycleRect;
    private Checkbox _reticleEnabled;
    private Checkbox _toggleCrouchEnabled;
    private Checkbox _indicatorsEnabled;
    private Rectangle _reticleStyleBox;
    private bool _reticleStyleOpen;
    private Rectangle _reticleColorBox;
    private bool _reticleColorOpen;
    private Rectangle _blockOutlineColorBox;
    private bool _blockOutlineColorOpen;
    private Checkbox _flyingOutlineEnabled;
    private Rectangle _flyingOutlineColorBox;
    private bool _flyingOutlineColorOpen;
    private Rectangle _nametagModeBox;
    private bool _nametagModeOpen;
    private Slider _nametagFadeSeconds;
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
    private string _applyFeedbackText = string.Empty;
    private float _applyFeedbackTimer;
    private bool _applyFeedbackIsError;
    private string _lastLayoutSnapshot = string.Empty;
    private bool _scrollbarDragging;
    private Tab _scrollbarDragTab;
    private int _scrollbarDragOffsetY;

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
        _toggleCrouchEnabled = new Checkbox("TOGGLE CROUCH", _working.ToggleCrouchEnabled, v =>
        {
            _working.ToggleCrouchEnabled = v;
            _log.Info($"Option changed: ToggleCrouchEnabled = {v}");
        });
        _reticleEnabled = new Checkbox("RETICLE", _working.ReticleEnabled, v =>
        {
            _working.ReticleEnabled = v;
            _log.Info($"Option changed: ReticleEnabled = {v}");
        });
        _indicatorsEnabled = new Checkbox("INDICATORS", _working.IndicatorsEnabled, v =>
        {
            _working.IndicatorsEnabled = v;
            _log.Info($"Option changed: IndicatorsEnabled = {v}");
        });
        _flyingOutlineEnabled = new Checkbox("FLY OUTLINE", _working.FlyingOutlineEnabled, v =>
        {
            _working.FlyingOutlineEnabled = v;
            _log.Info($"Option changed: FlyingOutlineEnabled = {v}");
        });
        _nametagFadeSeconds = new Slider("NAMETAG FADE", NametagFadeSecondsToSlider(_working.NametagFadeSeconds),
            v => _working.NametagFadeSeconds = SliderToNametagFadeSeconds(v),
            v =>
            {
                var value = SliderToNametagFadeSeconds(v);
                _working.NametagFadeSeconds = value;
                _log.Info($"Option changed: NametagFadeSeconds = {value:0.0}");
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

            // Load selected texture variants
            _videoSelectedTex = _assets.LoadTexture("textures/menu/buttons/VideoSelected.png");
            _audioSelectedTex = _assets.LoadTexture("textures/menu/buttons/AudioSelected.png");
            _controlsSelectedTex = _assets.LoadTexture("textures/menu/buttons/ControlsSelected.png");
            _packsSelectedTex = _assets.LoadTexture("textures/menu/buttons/packsSelected.png");

            _applyDefaultTexture = _assets.LoadTexture("textures/menu/buttons/Apply.png");
            _apply.Texture = _applyDefaultTexture;
            _applyVideoTexture = _applyDefaultTexture;
            _applyAudioTexture = _applyDefaultTexture;
            _back.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"OptionsScreen asset load: {ex.Message}");
        }
        UpdateTabTextures();
        UpdateApplyTexture();

        RefreshPacks();
        RefreshAudioDevices();
        CaptureVideoInfo();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(1248, viewport.Width - 20);
        var panelH = Math.Min(680, viewport.Height - 30);
        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);

        var layoutScale = Math.Clamp(Math.Min(panelW / 1180f, panelH / 680f), 0.82f, 1.0f);
        var sidePadding = (int)Math.Round(44f * layoutScale);
        var frameInsetX = Math.Max(sidePadding, (int)Math.Round(104f * layoutScale));
        var frameInsetTop = Math.Max((int)Math.Round(48f * layoutScale), (int)Math.Round(58f * layoutScale));
        var frameInsetBottom = Math.Max((int)Math.Round(24f * layoutScale), (int)Math.Round(30f * layoutScale));
        var sectionGap = (int)Math.Round(28f * layoutScale);
        var rowGap = (int)Math.Round(18f * layoutScale);
        var smallRowGap = (int)Math.Round(12f * layoutScale);
        var dropdownHeight = Math.Max(36, (int)Math.Round(40f * layoutScale));
        var checkboxHeight = Math.Max(34, (int)Math.Round(38f * layoutScale));
        var sliderHeight = Math.Max(16, (int)Math.Round(18f * layoutScale));
        var buttonHeight = Math.Max(48, (int)Math.Round(62f * layoutScale));
        var footerPadding = (int)Math.Round(16f * layoutScale);
        var tabGap = Math.Max(6, (int)Math.Round(8f * layoutScale));
        var tabY = _panelRect.Y - Math.Max(34, (int)Math.Round(42f * layoutScale));

        Rectangle FitAspectRect(int x, int y, int maxWidth, int targetHeight, Texture2D? texture, float fallbackAspect)
        {
            var aspect = texture is not null && texture.Height > 0
                ? texture.Width / (float)texture.Height
                : fallbackAspect;
            var width = Math.Min(maxWidth, Math.Max(1, (int)Math.Round(targetHeight * aspect)));
            return new Rectangle(x, y, width, targetHeight);
        }

        var tabGroupWidth = _panelRect.Width - frameInsetX * 2;
        var tabHeight = Math.Max(44, (int)Math.Round(Math.Min(70f, tabGroupWidth * 0.095f) * layoutScale));
        var videoTab = FitAspectRect(0, tabY, tabGroupWidth, tabHeight, _tabVideo.Texture, 3.25f);
        var audioTab = FitAspectRect(0, tabY, tabGroupWidth, tabHeight, _tabAudio.Texture, 3.25f);
        var controlsTab = FitAspectRect(0, tabY, tabGroupWidth, tabHeight, _tabControls.Texture, 3.25f);
        var packsTab = FitAspectRect(0, tabY, tabGroupWidth, tabHeight, _tabPacks.Texture, 3.25f);
        var actualTabGroupWidth = videoTab.Width + audioTab.Width + controlsTab.Width + packsTab.Width + tabGap * 3;
        var tabStartX = _panelRect.Center.X - actualTabGroupWidth / 2;
        _tabVideo.Bounds = new Rectangle(tabStartX, tabY, videoTab.Width, videoTab.Height);
        _tabAudio.Bounds = new Rectangle(_tabVideo.Bounds.Right + tabGap, tabY, audioTab.Width, audioTab.Height);
        _tabControls.Bounds = new Rectangle(_tabAudio.Bounds.Right + tabGap, tabY, controlsTab.Width, controlsTab.Height);
        _tabPacks.Bounds = new Rectangle(_tabControls.Bounds.Right + tabGap, tabY, packsTab.Width, packsTab.Height);

        var footerGap = Math.Max(16, (int)Math.Round(24f * layoutScale));
        var buttonMaxWidth = Math.Max(260, (int)Math.Round(320f * layoutScale));
        var backMargin = Math.Max(10, (int)Math.Round(12f * layoutScale));
        var applyBottomMargin = Math.Max(2, (int)Math.Round(4f * layoutScale));
        _back.Bounds = FitAspectRect(viewport.X + backMargin, viewport.Bottom - backMargin - buttonHeight, buttonMaxWidth, buttonHeight, _back.Texture, 3.6f);
        _apply.Bounds = FitAspectRect(_panelRect.Center.X - buttonMaxWidth / 2, viewport.Bottom - applyBottomMargin - buttonHeight, buttonMaxWidth, buttonHeight, _apply.Texture, 3.6f);

        var contentX = _panelRect.X + frameInsetX;
        var contentTop = _panelRect.Y + frameInsetTop;
        var contentY = contentTop + Math.Max(42, (int)Math.Round(48f * layoutScale));
        var contentW = panelW - frameInsetX * 2;
        var contentBottom = _apply.Bounds.Y - Math.Max(footerPadding, frameInsetBottom);
        var contentH = Math.Max(1, contentBottom - contentTop);
        var clipW = Math.Max(1, _panelRect.Width - frameInsetX * 2);
        _contentClipRect = new Rectangle(contentX, contentTop, clipW, contentH);

        var infoHeight = _font.LineHeight + 6;
        _videoInfoY = contentY;
        var videoContentY = contentY + infoHeight;

        var columnGap = sectionGap;
        var columnW = (contentW - columnGap) / 2;
        var leftX = contentX;
        var rightX = contentX + columnW + columnGap;
        var dropdownWidth = Math.Min(Math.Max(240, (int)Math.Round(380f * layoutScale)), columnW);
        var sliderWidth = Math.Min(Math.Max(260, (int)Math.Round(390f * layoutScale)), columnW);

        _fullscreen.Bounds = new Rectangle(leftX, videoContentY, columnW, checkboxHeight);
        _vsync.Bounds = new Rectangle(leftX, videoContentY + checkboxHeight + smallRowGap, columnW, checkboxHeight);

        _resolutionBox = new Rectangle(leftX, _vsync.Bounds.Bottom + rowGap, dropdownWidth, dropdownHeight);

        _guiScaleBox = new Rectangle(rightX, videoContentY, dropdownWidth, dropdownHeight);
        _qualityBox = new Rectangle(rightX, _guiScaleBox.Bottom + rowGap, dropdownWidth, dropdownHeight);
        _brightness.Bounds = new Rectangle(rightX, _qualityBox.Bottom + rowGap, sliderWidth, sliderHeight);
        _fov.Bounds = new Rectangle(rightX, _brightness.Bounds.Bottom + rowGap + _font.LineHeight, sliderWidth, sliderHeight);
        _renderDistance.Bounds = new Rectangle(rightX, _fov.Bounds.Bottom + rowGap + _font.LineHeight, sliderWidth, sliderHeight);
        _particleBox = new Rectangle(rightX, _renderDistance.Bounds.Bottom + rowGap + _font.LineHeight, dropdownWidth, dropdownHeight);

        var audioRightInset = Math.Max(44, (int)Math.Round(62f * layoutScale));
        var audioBoxW = Math.Max(340, contentW - audioRightInset);
        _inputBox = new Rectangle(contentX, contentY + 10, audioBoxW, dropdownHeight);
        _outputBox = new Rectangle(contentX, _inputBox.Bottom + rowGap, audioBoxW, dropdownHeight);
        _multistream.Bounds = new Rectangle(contentX, _outputBox.Bottom + rowGap, audioBoxW, checkboxHeight);

        var labelStartX = _multistream.Bounds.X + _multistream.Bounds.Height + 10;
        var labelWidth = (int)_font.MeasureString(_multistream.Label).X;
        var helpX = labelStartX + labelWidth + 10;
        if (helpX + 24 > _panelRect.Right - 10)
            helpX = _panelRect.Right - 34;
        _multistreamHelpRect = new Rectangle(helpX, _multistream.Bounds.Y + 4, 24, 24);

        var audioDetailY = _multistream.Bounds.Bottom + rowGap;
        int sliderStartY;
        if (_working.MultistreamAudio)
        {
            _voiceOutputBox = new Rectangle(contentX, audioDetailY, audioBoxW, dropdownHeight);
            _gameOutputBox = new Rectangle(contentX, _voiceOutputBox.Bottom + rowGap, audioBoxW, dropdownHeight);
            sliderStartY = _gameOutputBox.Bottom + rowGap;
        }
        else
        {
            _voiceOutputBox = Rectangle.Empty;
            _gameOutputBox = Rectangle.Empty;
            sliderStartY = audioDetailY;
        }

        var audioSliderWidth = Math.Min(Math.Max(300, (int)Math.Round(390f * layoutScale)), contentW);
        _master.Bounds = new Rectangle(contentX, sliderStartY, audioSliderWidth, sliderHeight);
        _music.Bounds = new Rectangle(contentX, _master.Bounds.Bottom + rowGap + _font.LineHeight, audioSliderWidth, sliderHeight);
        _sfx.Bounds = new Rectangle(contentX, _music.Bounds.Bottom + rowGap + _font.LineHeight, audioSliderWidth, sliderHeight);

        var micStartY = _sfx.Bounds.Bottom + rowGap + _font.LineHeight;
        var micButtonW = Math.Min(Math.Max(220, (int)Math.Round(260f * layoutScale)), contentW);
        _micTest.Bounds = new Rectangle(contentX, micStartY, micButtonW, dropdownHeight);
        _micMonitor.Bounds = new Rectangle(contentX, _micTest.Bounds.Bottom + smallRowGap, audioBoxW, checkboxHeight);
        _micMeterRect = new Rectangle(contentX, _micMonitor.Bounds.Bottom + rowGap, audioSliderWidth, sliderHeight);

        var contentBottomLimit = _micMeterRect.Bottom + Math.Max(6, (int)Math.Round(8f * layoutScale));
        _contentClipRect = new Rectangle(contentX, contentTop, clipW, Math.Max(1, contentBottomLimit - contentTop));

        var controlsListWidth = Math.Min(Math.Max(640, (int)Math.Round(760f * layoutScale)), contentW);
        var controlsValueWidth = Math.Min(Math.Max(460, (int)Math.Round(560f * layoutScale)), contentW);
        var controlsX = _panelRect.Center.X - controlsListWidth / 2;
        var controlsSubTabY = contentTop + Math.Max(24, (int)Math.Round(28f * layoutScale));
        var controlsSubTabGap = Math.Max(10, (int)Math.Round(14f * layoutScale));
        var controlsSubTabWidth = Math.Clamp((controlsListWidth - (controlsSubTabGap * 3)) / 4, 140, 240);
        var controlsTabsTotalWidth = controlsSubTabWidth * 4 + controlsSubTabGap * 3;
        var controlsTabsStartX = _panelRect.Center.X - controlsTabsTotalWidth / 2;
        var controlsTabHeight = Math.Max(36, (int)Math.Round(38f * layoutScale));
        _controlsMovementTabRect = new Rectangle(controlsTabsStartX, controlsSubTabY, controlsSubTabWidth, controlsTabHeight);
        _controlsGameplayTabRect = new Rectangle(_controlsMovementTabRect.Right + controlsSubTabGap, controlsSubTabY, controlsSubTabWidth, 38);
        _controlsGameplayTabRect = new Rectangle(_controlsMovementTabRect.Right + controlsSubTabGap, controlsSubTabY, controlsSubTabWidth, controlsTabHeight);
        _controlsInterfaceTabRect = new Rectangle(_controlsGameplayTabRect.Right + controlsSubTabGap, controlsSubTabY, controlsSubTabWidth, controlsTabHeight);
        _controlsAllBindsTabRect = new Rectangle(_controlsInterfaceTabRect.Right + controlsSubTabGap, controlsSubTabY, controlsSubTabWidth, controlsTabHeight);

        var controlsBodyTop = _controlsMovementTabRect.Bottom + Math.Max(10, (int)Math.Round(14f * layoutScale));
        _controlsBodyClipRect = new Rectangle(
            _contentClipRect.X,
            controlsBodyTop,
            _contentClipRect.Width,
            Math.Max(1, _contentClipRect.Bottom - controlsBodyTop));

        var controlsTop = controlsBodyTop + Math.Max(14, (int)Math.Round(18f * layoutScale));
        var controlsValueX = _panelRect.Center.X - controlsValueWidth / 2;
        _mouseSensitivity.Bounds = new Rectangle(controlsValueX, controlsTop, controlsValueWidth, sliderHeight);
        _crouchModeCycleRect = new Rectangle(controlsValueX, controlsTop + 48, controlsValueWidth, 42);
        _sprintModeCycleRect = new Rectangle(controlsValueX, controlsTop + 102, controlsValueWidth, 42);
        _toggleCrouchEnabled.Bounds = _crouchModeCycleRect;

        var reticleTop = controlsTop + 4;
        _reticleEnabled.Bounds = new Rectangle(controlsValueX, reticleTop, controlsValueWidth, 42);
        _reticleStyleBox = new Rectangle(controlsValueX, reticleTop + 62, controlsValueWidth, dropdownHeight);
        _reticleColorBox = new Rectangle(controlsValueX, reticleTop + 122, controlsValueWidth, dropdownHeight);
        _blockOutlineColorBox = new Rectangle(controlsValueX, reticleTop + 182, controlsValueWidth, dropdownHeight);
        _flyingOutlineEnabled.Bounds = new Rectangle(controlsValueX, reticleTop + 242, controlsValueWidth, 42);
        _flyingOutlineColorBox = new Rectangle(controlsValueX, reticleTop + 302, controlsValueWidth, dropdownHeight);
        _notificationModeBox = new Rectangle(controlsValueX, reticleTop + 362, controlsValueWidth, dropdownHeight);
        _indicatorsEnabled.Bounds = new Rectangle(controlsValueX, reticleTop + 422, controlsValueWidth, 42);
        _nametagModeBox = new Rectangle(controlsValueX, reticleTop + 482, controlsValueWidth, dropdownHeight);
        _nametagFadeSeconds.Bounds = new Rectangle(controlsValueX, reticleTop + 542, controlsValueWidth, sliderHeight);
        _reticleSize.Bounds = new Rectangle(controlsValueX, reticleTop + 602, controlsValueWidth, sliderHeight);
        _reticleThickness.Bounds = new Rectangle(controlsValueX, reticleTop + 662, controlsValueWidth, sliderHeight);

        var controlsListHeight = Math.Max(200, _controlsBodyClipRect.Height - 24);
        _controlsListRect = new Rectangle(controlsX, controlsTop + 200, controlsListWidth, controlsListHeight);
        _packsListRect = new Rectangle(contentX, contentY + 20, Math.Min(Math.Max(420, (int)Math.Round(520f * layoutScale)), contentW), 300);

        ClampAllScroll();
        LogLayoutSnapshot(layoutScale, frameInsetX, frameInsetTop, frameInsetBottom);
    }

    private void LogLayoutSnapshot(float layoutScale, int frameInsetX, int frameInsetTop, int frameInsetBottom)
    {
        var snapshot = string.Join(" | ",
            $"viewport={FormatRect(_viewport)}",
            $"panel={FormatRect(_panelRect)}",
            $"clip={FormatRect(_contentClipRect)}",
            $"tabs=V{FormatRect(_tabVideo.Bounds)} A{FormatRect(_tabAudio.Bounds)} C{FormatRect(_tabControls.Bounds)} P{FormatRect(_tabPacks.Bounds)}",
            $"videoLeft={FormatRect(_fullscreen.Bounds)}->{FormatRect(_resolutionBox)}",
            $"videoRight={FormatRect(_guiScaleBox)}->{FormatRect(_particleBox)}",
            $"footer=back{FormatRect(_back.Bounds)} apply{FormatRect(_apply.Bounds)}",
            $"layoutScale={layoutScale:0.000}",
            $"frameInset=({frameInsetX},{frameInsetTop},{frameInsetBottom})");

        if (string.Equals(snapshot, _lastLayoutSnapshot, StringComparison.Ordinal))
            return;

        _lastLayoutSnapshot = snapshot;
        _log.Info($"Options layout: {snapshot}");
    }

    private static string FormatRect(Rectangle rect) =>
        $"{rect.X},{rect.Y},{rect.Width}x{rect.Height}";

    public void Update(GameTime gameTime, InputState input)
    {
        if (input.IsNewKeyPress(Keys.Escape))
        {
            ExitOptions();
            return;
        }

        _lastMouse = input.MousePosition;

        ApplyPendingAudioRefresh();
        if (_applyFeedbackTimer > 0f)
        {
            _applyFeedbackTimer = Math.Max(0f, _applyFeedbackTimer - (float)gameTime.ElapsedGameTime.TotalSeconds);
            if (_applyFeedbackTimer <= 0f)
                _applyFeedbackText = string.Empty;
        }

        _tabVideo.Update(input);
        _tabAudio.Update(input);
        _tabControls.Update(input);
        _tabPacks.Update(input);

        _apply.Update(input);
        _back.Update(input);

        HandleScroll(input);
        UpdateScrollbarDrag(input);

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
                UpdateParticleDropdown(input);
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
                UpdateControlsSubTab(input);
                if (_controlsSubTab == ControlsSubTab.Movement)
                {
                    UpdateSliderWithScroll(_mouseSensitivity, input);
                    UpdateCrouchModeCycle(input);
                    UpdateSprintModeCycle(input);
                }
                else if (_controlsSubTab == ControlsSubTab.Interface)
                {
                    UpdateCheckboxWithScroll(_reticleEnabled, input);
                    UpdateCheckboxWithScroll(_indicatorsEnabled, input);
                    UpdateCheckboxWithScroll(_flyingOutlineEnabled, input);
                    UpdateReticleStyleDropdown(input);
                    UpdateReticleColorDropdown(input);
                    UpdateBlockOutlineColorDropdown(input);
                    UpdateFlyingOutlineColorDropdown(input);
                    UpdateNotificationModeDropdown(input);
                    UpdateNametagModeDropdown(input);
                    UpdateSliderWithScroll(_nametagFadeSeconds, input);
                    UpdateSliderWithScroll(_reticleSize, input);
                    UpdateSliderWithScroll(_reticleThickness, input);
                }
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

        // Draw tabs behind the panel
        _tabVideo.Draw(sb, _pixel, _font);
        _tabAudio.Draw(sb, _pixel, _font);
        _tabControls.Draw(sb, _pixel, _font);
        _tabPacks.Draw(sb, _pixel, _font);

        _apply.Draw(sb, _pixel, _font);
        _back.Draw(sb, _pixel, _font);
        DrawApplyFeedback(sb);

        // Content header - removed section text

        sb.End();

        var device = _graphics.GraphicsDevice;
        if (device == null)
            return;

        var priorScissor = device.ScissorRectangle;
        if (_tab == Tab.Controls)
        {
            device.ScissorRectangle = UiLayout.ToScreenRect(_contentClipRect);
            sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform, rasterizerState: ScissorState);
            DrawControlsSubTabs(sb);
            sb.End();

            device.ScissorRectangle = UiLayout.ToScreenRect(_controlsBodyClipRect);
            sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform, rasterizerState: ScissorState);
            DrawControls(sb);
            sb.End();
        }
        else
        {
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
                case Tab.Packs:
                    DrawPacks(sb);
                    break;
            }

            sb.End();
        }

        device.ScissorRectangle = priorScissor;

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        DrawScrollbars(sb);
        sb.End();
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
        var particleIndex = GetParticlePresetIndex(_working.ParticlePreset);
        var particleBox = ScrollRect(_particleBox, scroll);
        DrawSimpleDropdownBox(sb, particleBox, "PARTICLES", _particleOpen, ParticlePresets, particleIndex);

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
        if (_particleOpen)
            DrawSimpleDropdownList(sb, particleBox, ParticlePresets, particleIndex, openAbove: true);
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

    private void UpdateParticleDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_particleBox, scroll);
        UpdateSimpleDropdown(input, box, ref _particleOpen, ParticlePresets, idx =>
        {
            _working.ParticlePreset = ParticlePresets[idx];
            _log.Info($"Option changed: ParticlePreset = {_working.ParticlePreset}");
        }, openAbove: true);
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

    private void UpdateFlyingOutlineColorDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_flyingOutlineColorBox, scroll);
        UpdateSimpleDropdown(input, box, ref _flyingOutlineColorOpen, ReticleColorLabels, idx =>
        {
            _working.FlyingOutlineColor = ReticleColorOptions[idx].hex;
            _log.Info($"Option changed: FlyingOutlineColor = {_working.FlyingOutlineColor}");
        });
    }

    private void UpdateNotificationModeDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_notificationModeBox, scroll);
        UpdateSimpleDropdown(input, box, ref _notificationModeOpen, NotificationModeLabels, idx =>
        {
            var selected = NotificationModeOptions[Math.Clamp(idx, 0, NotificationModeOptions.Length - 1)].value;
            _working.SetSocialNotificationMode(selected);
            _log.Info($"Option changed: SocialNotifications = {_working.SocialNotifications}");
        });
    }

    private void UpdateNametagModeDropdown(InputState input)
    {
        var scroll = GetScrollOffset();
        var box = ScrollRect(_nametagModeBox, scroll);
        UpdateSimpleDropdown(input, box, ref _nametagModeOpen, NametagModeLabels, idx =>
        {
            _working.NametagMode = idx switch
            {
                0 => "Static",
                1 => "Fade",
                _ => "Off"
            };
            _log.Info($"Option changed: NametagMode = {_working.NametagMode}");
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
        var visibleActions = GetVisibleBindActions();
        var scroll = GetScrollOffset();
        var listRect = ScrollRect(_controlsListRect, scroll);
        if (_bindingAction is null)
        {
            if (input.IsNewLeftClick())
            {
                var p = input.MousePosition;
                if (listRect.Contains(p))
                {
                    var rowH = 36;
                    var idx = (p.Y - listRect.Y + scroll) / rowH;
                    if (idx >= 0 && idx < visibleActions.Count)
                        _bindingAction = visibleActions[idx];
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

            var mouseBind = GetNewMouseBindToken(input);
            if (!string.IsNullOrWhiteSpace(mouseBind))
            {
                _working.MouseBinds[_bindingAction] = mouseBind;
                _log.Info($"Option changed: Mouse bind {_bindingAction} = {mouseBind}");
                _bindingAction = null;
                return;
            }

            foreach (var k in input.GetNewKeys())
            {
                if ((k == Keys.LeftShift || k == Keys.RightShift)
                    && !string.Equals(_bindingAction, "Crouch", StringComparison.Ordinal)
                    && !string.Equals(_bindingAction, "Sprint", StringComparison.Ordinal)
                    && !string.Equals(_bindingAction, "FlyDescend", StringComparison.Ordinal)
                    && !string.Equals(_bindingAction, "GamemodeModifier", StringComparison.Ordinal))
                    continue;
                _working.Keybinds[_bindingAction] = k;
                _working.MouseBinds.Remove(_bindingAction);
                _log.Info($"Option changed: Keybind {_bindingAction} = {k}");
                _bindingAction = null;
                break;
            }
        }
    }

    private void UpdateCrouchModeCycle(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var rect = ScrollRect(_crouchModeCycleRect);
        if (!rect.Contains(input.MousePosition))
            return;

        _working.ToggleCrouchEnabled = !_working.ToggleCrouchEnabled;
        _toggleCrouchEnabled.Value = _working.ToggleCrouchEnabled;
        _log.Info($"Option changed: ToggleCrouchEnabled = {_working.ToggleCrouchEnabled}");
    }

    private void UpdateSprintModeCycle(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var rect = ScrollRect(_sprintModeCycleRect);
        if (!rect.Contains(input.MousePosition))
            return;

        _working.SprintLatchEnabled = !_working.SprintLatchEnabled;
        _log.Info($"Option changed: SprintLatchEnabled = {_working.SprintLatchEnabled}");
    }

    private void DrawModeCycleRowWithScroll(SpriteBatch sb, Rectangle rowBounds, string label, string value)
    {
        var rect = ScrollRect(rowBounds, GetScrollOffset());
        sb.Draw(_pixel, rect, new Color(16, 20, 28, 220));
        DrawBorder(sb, rect, new Color(136, 146, 168), 1);

        _font.DrawString(sb, label, new Vector2(rect.X + 12, rect.Y + 11), new Color(224, 232, 245));

        var valueBox = new Rectangle(rect.Right - 210, rect.Y + 6, 198, rect.Height - 12);
        sb.Draw(_pixel, valueBox, new Color(28, 36, 56, 235));
        DrawBorder(sb, valueBox, new Color(212, 226, 245), 1);
        var valueText = $"< {value} >";
        var valueSize = _font.MeasureString(valueText);
        var valuePos = new Vector2(
            valueBox.X + (valueBox.Width - valueSize.X) * 0.5f,
            valueBox.Y + (valueBox.Height - valueSize.Y) * 0.5f);
        _font.DrawString(sb, valueText, valuePos, new Color(242, 248, 255));
    }

    private void DrawBinarySwitchRowWithScroll(SpriteBatch sb, Rectangle rowBounds, string label, bool enabled)
    {
        var rect = ScrollRect(rowBounds, GetScrollOffset());
        var rowFill = enabled
            ? new Color(18, 36, 24, 220)
            : new Color(24, 20, 20, 220);
        var rowBorder = enabled
            ? new Color(152, 236, 172)
            : new Color(236, 152, 152);
        sb.Draw(_pixel, rect, rowFill);
        DrawBorder(sb, rect, rowBorder, 1);

        _font.DrawString(sb, label, new Vector2(rect.X + 12, rect.Y + 11), new Color(232, 238, 245));

        var trackWidth = Math.Clamp(rect.Width / 4, 120, 170);
        var trackHeight = Math.Clamp(rect.Height - 12, 20, 30);
        var trackRect = new Rectangle(rect.Right - trackWidth - 10, rect.Y + (rect.Height - trackHeight) / 2, trackWidth, trackHeight);
        sb.Draw(_pixel, trackRect, new Color(10, 12, 18, 230));
        DrawBorder(sb, trackRect, new Color(180, 190, 210), 1);

        var offColor = enabled ? new Color(120, 120, 120) : new Color(255, 214, 214);
        var onColor = enabled ? new Color(214, 255, 224) : new Color(120, 120, 120);
        _font.DrawString(sb, "OFF", new Vector2(trackRect.X + 8, trackRect.Y + 4), offColor);
        var onSize = _font.MeasureString("ON");
        _font.DrawString(sb, "ON", new Vector2(trackRect.Right - onSize.X - 8, trackRect.Y + 4), onColor);

        var knobWidth = (trackRect.Width / 2) - 6;
        var knobRect = new Rectangle(
            enabled ? trackRect.Right - knobWidth - 3 : trackRect.X + 3,
            trackRect.Y + 3,
            knobWidth,
            trackRect.Height - 6);
        var knobColor = enabled ? new Color(98, 198, 122) : new Color(206, 108, 108);
        sb.Draw(_pixel, knobRect, knobColor);
        DrawBorder(sb, knobRect, new Color(240, 240, 240), 1);
    }

    private void DrawControls(SpriteBatch sb)
    {
        var scroll = GetScrollOffset();

        if (_controlsSubTab == ControlsSubTab.Movement)
        {
            DrawSliderWithScroll(_mouseSensitivity, sb);

            var sensRect = ScrollRect(_mouseSensitivity.Bounds, scroll);
            _font.DrawString(sb, $"SENS: {_working.MouseSensitivity:0.0000}", new Vector2(sensRect.Right + 20, sensRect.Y - _font.LineHeight + 2), Color.White);
            DrawModeCycleRowWithScroll(
                sb,
                _crouchModeCycleRect,
                "CROUCH MODE",
                _working.ToggleCrouchEnabled ? "TOGGLE" : "HOLD");
            DrawBinarySwitchRowWithScroll(sb, _sprintModeCycleRect, "AUTO SPRINT", _working.SprintLatchEnabled);
            var sprintRect = ScrollRect(_sprintModeCycleRect, scroll);
            _font.DrawString(sb, "OFF=HOLD | ON=LATCH", new Vector2(sprintRect.X, sprintRect.Bottom + 6), new Color(195, 210, 230));
        }
        else if (_controlsSubTab == ControlsSubTab.Interface)
        {
            DrawCheckboxWithScroll(_reticleEnabled, sb);
            DrawCheckboxWithScroll(_indicatorsEnabled, sb);
            DrawCheckboxWithScroll(_flyingOutlineEnabled, sb);

            var styleBox = ScrollRect(_reticleStyleBox, scroll);
            var styleIndex = GetReticleStyleIndex(_working.ReticleStyle);
            DrawSimpleDropdownBox(sb, styleBox, "RETICLE STYLE", _reticleStyleOpen, ReticleStyleLabels, styleIndex);

            var colorBox = ScrollRect(_reticleColorBox, scroll);
            var colorIndex = GetReticleColorIndex(_working.ReticleColor);
            DrawColorDropdownBox(sb, colorBox, "RETICLE COLOR", _reticleColorOpen, colorIndex);

            var outlineBox = ScrollRect(_blockOutlineColorBox, scroll);
            var outlineIndex = GetReticleColorIndex(_working.BlockOutlineColor);
            DrawColorDropdownBox(sb, outlineBox, "BLOCK OUTLINE", _blockOutlineColorOpen, outlineIndex);

            var flyingOutlineBox = ScrollRect(_flyingOutlineColorBox, scroll);
            var flyingOutlineIndex = GetReticleColorIndex(_working.FlyingOutlineColor);
            DrawColorDropdownBox(sb, flyingOutlineBox, "FLY OUTLINE COLOR", _flyingOutlineColorOpen, flyingOutlineIndex);

            var notificationBox = ScrollRect(_notificationModeBox, scroll);
            var notificationModeIndex = GetNotificationModeIndex(_working.GetSocialNotificationMode());
            DrawSimpleDropdownBox(sb, notificationBox, "NOTIFICATIONS", _notificationModeOpen, NotificationModeLabels, notificationModeIndex);

            var nametagBox = ScrollRect(_nametagModeBox, scroll);
            var nametagModeIndex = GetNametagModeIndex(_working.NametagMode);
            DrawSimpleDropdownBox(sb, nametagBox, "NAMETAGS", _nametagModeOpen, NametagModeLabels, nametagModeIndex);

            DrawSliderWithScroll(_nametagFadeSeconds, sb);
            var nameFadeRect = ScrollRect(_nametagFadeSeconds.Bounds, scroll);
            _font.DrawString(sb, $"FADE SECS: {_working.NametagFadeSeconds:0.0}", new Vector2(nameFadeRect.Right + 20, nameFadeRect.Y - _font.LineHeight + 2), Color.White);
            var nametagHint = _working.NametagMode switch
            {
                "Fade" => "FADE: HOLD TAB TO SHOW NAMES, THEN THEY FADE OUT.",
                "Off" => "OFF: PLAYER NAMETAGS ARE HIDDEN.",
                _ => "STATIC: PLAYER NAMETAGS ARE ALWAYS VISIBLE."
            };
            _font.DrawString(sb, nametagHint, new Vector2(nametagBox.X, nameFadeRect.Bottom + 4), new Color(185, 198, 220));

            DrawSliderWithScroll(_reticleSize, sb);
            DrawSliderWithScroll(_reticleThickness, sb);
            var sizeRect = ScrollRect(_reticleSize.Bounds, scroll);
            _font.DrawString(sb, $"SIZE: {_working.ReticleSize}", new Vector2(sizeRect.Right + 20, sizeRect.Y - _font.LineHeight + 2), Color.White);
            var thickRect = ScrollRect(_reticleThickness.Bounds, scroll);
            _font.DrawString(sb, $"THICK: {_working.ReticleThickness}", new Vector2(thickRect.Right + 20, thickRect.Y - _font.LineHeight + 2), Color.White);

            if (_reticleStyleOpen)
                DrawSimpleDropdownList(sb, styleBox, ReticleStyleLabels, styleIndex);
            if (_reticleColorOpen)
                DrawColorDropdownList(sb, colorBox, colorIndex);
            if (_blockOutlineColorOpen)
                DrawColorDropdownList(sb, outlineBox, outlineIndex);
            if (_flyingOutlineColorOpen)
                DrawColorDropdownList(sb, flyingOutlineBox, flyingOutlineIndex);
            if (_notificationModeOpen)
                DrawSimpleDropdownList(sb, notificationBox, NotificationModeLabels, notificationModeIndex);
            if (_nametagModeOpen)
                DrawSimpleDropdownList(sb, nametagBox, NametagModeLabels, nametagModeIndex);
        }

        DrawBindList(sb, scroll, GetVisibleBindActions(), _controlsSubTab == ControlsSubTab.AllBinds);
    }

    private void UpdateControlsSubTab(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var click = input.MousePosition;
        if (_controlsMovementTabRect.Contains(click))
            SetControlsSubTab(ControlsSubTab.Movement);
        else if (_controlsGameplayTabRect.Contains(click))
            SetControlsSubTab(ControlsSubTab.Gameplay);
        else if (_controlsInterfaceTabRect.Contains(click))
            SetControlsSubTab(ControlsSubTab.Interface);
        else if (_controlsAllBindsTabRect.Contains(click))
            SetControlsSubTab(ControlsSubTab.AllBinds);
    }

    private void SetControlsSubTab(ControlsSubTab tab)
    {
        if (_controlsSubTab == tab)
            return;

        _controlsSubTab = tab;
        _bindingAction = null;
        CloseAllDropdowns();
    }

    private void DrawControlsSubTabs(SpriteBatch sb)
    {
        DrawControlsSubTabButton(sb, _controlsMovementTabRect, "MOVEMENT", _controlsSubTab == ControlsSubTab.Movement);
        DrawControlsSubTabButton(sb, _controlsGameplayTabRect, "GAMEPLAY", _controlsSubTab == ControlsSubTab.Gameplay);
        DrawControlsSubTabButton(sb, _controlsInterfaceTabRect, "INTERFACE", _controlsSubTab == ControlsSubTab.Interface);
        DrawControlsSubTabButton(sb, _controlsAllBindsTabRect, "ALL BINDS", _controlsSubTab == ControlsSubTab.AllBinds);
    }

    private void DrawControlsSubTabButton(SpriteBatch sb, Rectangle rect, string label, bool selected)
    {
        var fill = selected ? new Color(40, 56, 86, 235) : new Color(16, 20, 28, 220);
        var border = selected ? new Color(226, 240, 255) : new Color(120, 132, 150);
        var text = selected ? new Color(245, 250, 255) : new Color(210, 220, 235);
        sb.Draw(_pixel, rect, fill);
        DrawBorder(sb, rect, border, 1);
        var textSize = _font.MeasureString(label);
        var textPos = new Vector2(rect.X + (rect.Width - textSize.X) * 0.5f, rect.Y + (rect.Height - textSize.Y) * 0.5f);
        _font.DrawString(sb, label, textPos, text);
    }

    private IReadOnlyList<string> GetVisibleBindActions()
    {
        return _controlsSubTab switch
        {
            ControlsSubTab.Movement => MovementBindActions,
            ControlsSubTab.Gameplay => GameplayBindActions,
            ControlsSubTab.Interface => InterfaceBindActions,
            _ => _bindOrder
        };
    }

    private Rectangle GetCurrentBindListRect(int scroll)
    {
        var listRect = ScrollRect(_controlsListRect, scroll);
        return new Rectangle(listRect.X, GetCurrentBindListTop() - scroll, listRect.Width, listRect.Height);
    }

    private void DrawBindList(SpriteBatch sb, int scroll, IReadOnlyList<string> actions, bool drawExtendedNotes)
    {
        var listRect = GetCurrentBindListRect(scroll);
        _font.DrawString(sb, "CLICK AN ACTION TO REBIND", new Vector2(listRect.X, listRect.Y - _font.LineHeight), Color.White);

        var rowH = 36;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var y = listRect.Y + i * rowH;
            var row = new Rectangle(listRect.X, y, listRect.Width, rowH - 4);
            sb.Draw(_pixel, row, new Color(18, 18, 18));
            DrawBorder(sb, row, Color.White);

            _font.DrawString(sb, GetBindActionLabel(action), new Vector2(row.X + 10, row.Y + 10), Color.White);

            var key = GetDisplayedBindingLabel(action);
            var keyText = _bindingAction == action ? "PRESS KEY..." : key.ToUpperInvariant();
            _font.DrawString(sb, keyText, new Vector2(row.Right - 220, row.Y + 10), Color.White);
        }

        if (drawExtendedNotes)
        {
            var modeModifierLabel = _working.Keybinds.TryGetValue("GamemodeModifier", out var modeModifierKey)
                ? modeModifierKey.ToString().ToUpperInvariant()
                : "ALT";
            var notes = new (string text, Color color)[]
            {
                ("NON-REBINDABLE: HOLD TAB = PLAYER LIST", new Color(210, 210, 210)),
                ($"MOUSE: LEFT BREAK/USE | RIGHT PLACE/USE | WHEEL HOTBAR | HOLD {modeModifierLabel}+WHEEL (FLYING) FOR FLY SPEED", new Color(180, 180, 180)),
                ("VEILSEER: 1-9 SPECTATE PLAYER | 0/SHIFT DETACH TO FREECAM", new Color(180, 180, 180)),
                ("HOME GUI: PENCIL TO RENAME | DRAG HOTBAR OR PRESS 1-9 OVER A HOME FOR ICON", new Color(180, 180, 180)),
                ("UI: ENTER SENDS CHAT/COMMAND | ESC UNFOCUSES TEXT FIELD FIRST", new Color(180, 180, 180))
            };

            var notesY = listRect.Y + actions.Count * rowH + 8;
            var lineAdvance = _font.LineHeight + 2;
            var notesMaxWidth = Math.Max(220, listRect.Width - 16);
            foreach (var (text, color) in notes)
            {
                foreach (var wrappedLine in WrapText(text, notesMaxWidth))
                {
                    _font.DrawString(sb, wrappedLine, new Vector2(listRect.X, notesY), color);
                    notesY += lineAdvance;
                }
            }
        }
    }

    private void DrawApplyFeedback(SpriteBatch sb)
    {
        if (_applyFeedbackTimer <= 0f || string.IsNullOrWhiteSpace(_applyFeedbackText))
            return;

        var alpha = Math.Clamp(_applyFeedbackTimer / 2.4f, 0f, 1f);
        var border = _applyFeedbackIsError
            ? new Color(255, 138, 138, Math.Clamp((int)(220f * alpha), 0, 255))
            : new Color(168, 242, 186, Math.Clamp((int)(220f * alpha), 0, 255));
        var textColor = _applyFeedbackIsError
            ? new Color(255, 214, 214, Math.Clamp((int)(255f * alpha), 0, 255))
            : new Color(226, 255, 232, Math.Clamp((int)(255f * alpha), 0, 255));
        var bg = new Color(0, 0, 0, Math.Clamp((int)(168f * alpha), 0, 255));

        var size = _font.MeasureString(_applyFeedbackText);
        var width = (int)Math.Ceiling(size.X) + 28;
        var height = _font.LineHeight + 14;
        var rect = new Rectangle(_apply.Bounds.Center.X - width / 2, _apply.Bounds.Y - height - 12, width, height);
        sb.Draw(_pixel, rect, bg);
        DrawBorder(sb, rect, border, 1);
        var textPos = new Vector2(rect.X + 14, rect.Y + 7);
        _font.DrawString(sb, _applyFeedbackText, textPos, textColor);
        _font.DrawString(sb, _applyFeedbackText, textPos + new Vector2(1, 0), textColor);
    }

    private void RefreshPacks()
    {
        _availablePacks.Clear();

        try
        {
            var packsDir = Paths.PacksDir;
            Directory.CreateDirectory(packsDir);

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
        _font.DrawString(sb, "PACKS (Documents/LatticeVeil/Packs)", new Vector2(listRect.X, listRect.Y - _font.LineHeight), Color.White);

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

    private string GetDisplayedBindingLabel(string action)
    {
        if (_working.MouseBinds != null
            && _working.MouseBinds.TryGetValue(action, out var mouseBind)
            && !string.IsNullOrWhiteSpace(mouseBind))
        {
            return mouseBind switch
            {
                "mouseleft" => "Mouse1",
                "mouseright" => "Mouse2",
                "mousemiddle" => "Mouse3",
                "mousex1" => "Mouse4",
                "mousex2" => "Mouse5",
                _ => mouseBind
            };
        }

        return _working.Keybinds.TryGetValue(action, out var key) ? key.ToString() : "UNBOUND";
    }

    private static string? GetNewMouseBindToken(InputState input)
    {
        if (input.IsNewLeftClick())
            return "mouseleft";
        if (input.IsNewRightClick())
            return "mouseright";
        if (input.IsNewMiddleClick())
            return "mousemiddle";
        if (input.IsNewXButton1Click())
            return "mousex1";
        if (input.IsNewXButton2Click())
            return "mousex2";
        return null;
    }

    private int GetCurrentBindListTop()
    {
        return _controlsSubTab switch
        {
            ControlsSubTab.Interface => _reticleThickness.Bounds.Bottom + 84,
            ControlsSubTab.Movement => _sprintModeCycleRect.Bottom + 62,
            _ => _controlsBodyClipRect.Y + 20
        };
    }

    private int GetCurrentBindListBottom()
    {
        const int rowH = 36;
        var bottom = GetCurrentBindListTop() + GetVisibleBindActions().Count * rowH;
        if (_controlsSubTab == ControlsSubTab.AllBinds)
            bottom += 8 + (GetAllBindsNoteLineCount() * (_font.LineHeight + 2));
        return bottom;
    }

    private int GetAllBindsNoteLineCount()
    {
        var modeModifierLabel = _working.Keybinds.TryGetValue("GamemodeModifier", out var modeModifierKey)
            ? modeModifierKey.ToString().ToUpperInvariant()
            : "ALT";
        var notes = new[]
        {
            "NON-REBINDABLE: HOLD TAB = PLAYER LIST",
            $"MOUSE: LEFT BREAK/USE | RIGHT PLACE/USE | WHEEL HOTBAR | HOLD {modeModifierLabel}+WHEEL (FLYING) FOR FLY SPEED",
            "VEILSEER: 1-9 SPECTATE PLAYER | 0/SHIFT DETACH TO FREECAM",
            "HOME GUI: PENCIL TO RENAME | DRAG HOTBAR OR PRESS 1-9 OVER A HOME FOR ICON",
            "UI: ENTER SENDS CHAT/COMMAND | ESC UNFOCUSES TEXT FIELD FIRST"
        };

        var notesMaxWidth = Math.Max(220, _controlsListRect.Width - 16);
        var total = 0;
        foreach (var note in notes)
            total += WrapText(note, notesMaxWidth).Count;
        return total;
    }

    private List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = words[0];
        for (int i = 1; i < words.Length; i++)
        {
            var candidate = $"{current} {words[i]}";
            if (_font.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = words[i];
        }

        lines.Add(current);
        return lines;
    }

    private void DrawScrollbars(SpriteBatch sb)
    {
        if (_tab == Tab.Controls)
            DrawScrollbar(sb, _controlsBodyClipRect, Tab.Controls);
    }

    private void DrawScrollbar(SpriteBatch sb, Rectangle viewportRect, Tab tab)
    {
        var maxScroll = GetMaxScroll(tab);
        if (maxScroll <= 0)
            return;

        var contentHeight = GetContentHeight(tab);
        if (contentHeight <= viewportRect.Height)
            return;

        var trackRect = GetScrollbarTrackRect(viewportRect);
        sb.Draw(_pixel, trackRect, new Color(10, 12, 18, 185));
        DrawBorder(sb, trackRect, new Color(120, 132, 150), 1);

        var thumbRect = GetScrollbarThumbRect(trackRect, viewportRect, tab, contentHeight, maxScroll);
        sb.Draw(_pixel, thumbRect, new Color(220, 226, 235, 210));
        DrawBorder(sb, thumbRect, Color.White, 1);
    }

    private static Rectangle GetScrollbarTrackRect(Rectangle viewportRect) =>
        new(viewportRect.Right - 14, viewportRect.Y + 4, 10, Math.Max(20, viewportRect.Height - 8));

    private Rectangle GetScrollbarThumbRect(Rectangle trackRect, Rectangle viewportRect, Tab tab, int contentHeight, int maxScroll)
    {
        var thumbHeight = Math.Max(24, (int)Math.Round(trackRect.Height * (viewportRect.Height / (float)contentHeight)));
        var thumbTravel = Math.Max(0, trackRect.Height - thumbHeight);
        var thumbY = trackRect.Y;
        if (thumbTravel > 0 && maxScroll > 0)
            thumbY += (int)Math.Round((GetScroll(tab) / maxScroll) * thumbTravel);
        return new Rectangle(trackRect.X + 1, thumbY + 1, trackRect.Width - 2, thumbHeight - 2);
    }

    private Rectangle GetScrollViewportRect(Tab tab) =>
        tab == Tab.Controls ? _controlsBodyClipRect : _contentClipRect;

    private void UpdateScrollbarDrag(InputState input)
    {
        if (_scrollbarDragging)
        {
            if (!input.IsLeftDown())
            {
                _scrollbarDragging = false;
                return;
            }

            var tab = _scrollbarDragTab;
            var viewportRect = GetScrollViewportRect(tab);
            var maxScroll = GetMaxScroll(tab);
            var contentHeight = GetContentHeight(tab);
            if (maxScroll <= 0 || contentHeight <= viewportRect.Height)
            {
                _scrollbarDragging = false;
                return;
            }

            var trackRect = GetScrollbarTrackRect(viewportRect);
            var thumbRect = GetScrollbarThumbRect(trackRect, viewportRect, tab, contentHeight, maxScroll);
            var minThumbY = trackRect.Y + 1;
            var maxThumbY = trackRect.Bottom - thumbRect.Height - 1;
            var desiredThumbY = Math.Clamp(input.MousePosition.Y - _scrollbarDragOffsetY, minThumbY, maxThumbY);
            var ratio = (desiredThumbY - minThumbY) / (float)Math.Max(1, maxThumbY - minThumbY);
            SetScroll(tab, ratio * maxScroll);
            ClampScroll(tab);
            return;
        }

        if (!input.IsNewLeftClick() || _tab != Tab.Controls)
            return;

        var viewport = GetScrollViewportRect(_tab);
        var max = GetMaxScroll(_tab);
        var contentHeightForTab = GetContentHeight(_tab);
        if (max <= 0 || contentHeightForTab <= viewport.Height)
            return;

        var track = GetScrollbarTrackRect(viewport);
        if (!track.Contains(input.MousePosition))
            return;

        var thumb = GetScrollbarThumbRect(track, viewport, _tab, contentHeightForTab, max);
        if (thumb.Contains(input.MousePosition))
        {
            _scrollbarDragging = true;
            _scrollbarDragTab = _tab;
            _scrollbarDragOffsetY = input.MousePosition.Y - thumb.Y;
            return;
        }

        var clickMinThumbY = track.Y + 1;
        var clickMaxThumbY = track.Bottom - thumb.Height - 1;
        var targetThumbY = Math.Clamp(input.MousePosition.Y - (thumb.Height / 2), clickMinThumbY, clickMaxThumbY);
        var ratioToClick = (targetThumbY - clickMinThumbY) / (float)Math.Max(1, clickMaxThumbY - clickMinThumbY);
        SetScroll(_tab, ratioToClick * max);
        ClampScroll(_tab);
        _scrollbarDragging = true;
        _scrollbarDragTab = _tab;
        _scrollbarDragOffsetY = input.MousePosition.Y - targetThumbY;
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
        _particleOpen = false;
        _inputOpen = false;
        _outputOpen = false;
        _voiceOutputOpen = false;
        _gameOutputOpen = false;
        _reticleStyleOpen = false;
        _reticleColorOpen = false;
        _blockOutlineColorOpen = false;
        _flyingOutlineColorOpen = false;
        _notificationModeOpen = false;
        _nametagModeOpen = false;
    }

    private void UpdateSimpleDropdown(InputState input, Rectangle box, ref bool open, IReadOnlyList<string> items, Action<int> onSelect, bool openAbove = false)
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

        var listRect = GetDropdownListRect(box, items.Count, openAbove);
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

    private void DrawSimpleDropdownList(SpriteBatch sb, Rectangle box, IReadOnlyList<string> items, int currentIndex, bool openAbove = false)
    {
        if (items.Count == 0)
            return;

        var listRect = GetDropdownListRect(box, items.Count, openAbove);
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

    private static Rectangle GetDropdownListRect(Rectangle box, int itemCount, bool openAbove = false)
    {
        var height = itemCount * DropdownItemHeight + 6;
        return openAbove
            ? new Rectangle(box.X, box.Y - 4 - height, box.Width, height)
            : new Rectangle(box.X, box.Bottom + 4, box.Width, height);
    }

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

        var activeClip = _tab == Tab.Controls ? _controlsBodyClipRect : _contentClipRect;
        if (!activeClip.Contains(input.MousePosition))
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
        var rect = ScrollRect(checkbox.Bounds);
        var rowFill = checkbox.Value
            ? new Color(18, 36, 24, 220)
            : new Color(24, 20, 20, 220);
        var rowBorder = checkbox.Value
            ? new Color(152, 236, 172)
            : new Color(236, 152, 152);
        sb.Draw(_pixel, rect, rowFill);
        DrawBorder(sb, rect, rowBorder, 1);

        _font.DrawString(sb, checkbox.Label, new Vector2(rect.X + 12, rect.Y + 10), new Color(232, 238, 245));

        var trackWidth = Math.Clamp(rect.Width / 4, 120, 170);
        var trackHeight = Math.Clamp(rect.Height - 12, 20, 30);
        var trackRect = new Rectangle(rect.Right - trackWidth - 10, rect.Y + (rect.Height - trackHeight) / 2, trackWidth, trackHeight);
        sb.Draw(_pixel, trackRect, new Color(10, 12, 18, 230));
        DrawBorder(sb, trackRect, new Color(180, 190, 210), 1);

        var offLabel = "OFF";
        var onLabel = "ON";
        var offColor = checkbox.Value ? new Color(120, 120, 120) : new Color(255, 214, 214);
        var onColor = checkbox.Value ? new Color(214, 255, 224) : new Color(120, 120, 120);
        _font.DrawString(sb, offLabel, new Vector2(trackRect.X + 8, trackRect.Y + 4), offColor);
        var onSize = _font.MeasureString(onLabel);
        _font.DrawString(sb, onLabel, new Vector2(trackRect.Right - onSize.X - 8, trackRect.Y + 4), onColor);

        var knobWidth = (trackRect.Width / 2) - 6;
        var knobRect = new Rectangle(
            checkbox.Value ? trackRect.Right - knobWidth - 3 : trackRect.X + 3,
            trackRect.Y + 3,
            knobWidth,
            trackRect.Height - 6);
        var knobColor = checkbox.Value ? new Color(98, 198, 122) : new Color(206, 108, 108);
        sb.Draw(_pixel, knobRect, knobColor);
        DrawBorder(sb, knobRect, new Color(240, 240, 240), 1);
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

        var viewportHeight = GetScrollViewportRect(tab).Height;
        var max = contentHeight - viewportHeight;
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
                bottom = Math.Max(bottom, _particleBox.Bottom);
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
                top = _controlsBodyClipRect.Y;
                var bottom = Math.Max(GetCurrentBindListBottom(), _controlsBodyClipRect.Bottom);
                bottom = Math.Max(bottom, _reticleThickness.Bounds.Bottom);
                bottom = Math.Max(bottom, _sprintModeCycleRect.Bottom);
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

    private static int GetNotificationModeIndex(SocialNotificationMode mode)
    {
        for (var i = 0; i < NotificationModeOptions.Length; i++)
        {
            if (NotificationModeOptions[i].value == mode)
                return i;
        }

        return NotificationModeOptions.Length - 1;
    }

    private static int GetNametagModeIndex(string? mode)
    {
        var text = (mode ?? string.Empty).Trim();
        if (text.Equals("fade", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (text.Equals("off", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 0;
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

    private static int GetParticlePresetIndex(string? value)
    {
        var normalized = NormalizeParticlePreset(value);
        for (int i = 0; i < ParticlePresets.Length; i++)
        {
            if (ParticlePresets[i] == normalized)
                return i;
        }

        return 0;
    }

    private static string GetBindActionLabel(string action)
    {
        return action switch
        {
            "MoveUp" => "MOVE FORWARD",
            "MoveDown" => "MOVE BACKWARD",
            "MoveLeft" => "MOVE LEFT",
            "MoveRight" => "MOVE RIGHT",
            "Jump" => "JUMP",
            "Crouch" => "CROUCH",
            "Sprint" => "SPRINT",
            "FlyDescend" => "FLY DESCEND",
            "Inventory" => "INVENTORY",
            "DropItem" => "DROP ITEM",
            "GiveItem" => "GIVE ITEM",
            "Pause" => "PAUSE",
            "Chat" => "OPEN CHAT",
            "Command" => "OPEN COMMAND (/)",
            "HomeGui" => "HOME GUI",
            "StructureFinder" => "FINDER GUI",
            "GamemodeModifier" => "MODE MODIFIER",
            "GamemodeWheel" => "MODE WHEEL",
            "VeilseerXrayToggle" => "VEILSEER XRAY TOGGLE",
            "InviteQuickAction" => "INVITE QUICK ACTION",
            _ => action.ToUpperInvariant()
        };
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

    private static string NormalizeParticlePreset(string? value)
    {
        var preset = string.IsNullOrWhiteSpace(value) ? "AUTO" : value.Trim().ToUpperInvariant();
        return preset is "AUTO" or "OFF" or "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? preset : "AUTO";
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

    private static float NametagFadeSecondsToSlider(float value) =>
        (Math.Clamp(value, 0.5f, 12f) - 0.5f) / 11.5f;

    private static float SliderToNametagFadeSeconds(float slider) =>
        0.5f + Clamp01(slider) * 11.5f;

    private void SnapWorkingVideoOptions()
    {
        _working.GuiScale = GuiScaleCandidates[GetGuiScaleIndex(_working.GuiScale)].value;
        _working.QualityPreset = NormalizeQuality(_working.QualityPreset);
        _working.ParticlePreset = NormalizeParticlePreset(_working.ParticlePreset);
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
            _applyFeedbackText = "SETTINGS APPLIED";
            _applyFeedbackTimer = 2.4f;
            _applyFeedbackIsError = false;
        }
        catch (Exception ex)
        {
            _log.Error($"Apply settings failed: {ex.Message}");
            _applyFeedbackText = "APPLY FAILED";
            _applyFeedbackTimer = 2.4f;
            _applyFeedbackIsError = true;
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
        _toggleCrouchEnabled.Value = _working.ToggleCrouchEnabled;
        _reticleEnabled.Value = _working.ReticleEnabled;
        _indicatorsEnabled.Value = _working.IndicatorsEnabled;
        _flyingOutlineEnabled.Value = _working.FlyingOutlineEnabled;
        _nametagFadeSeconds.Value = NametagFadeSecondsToSlider(_working.NametagFadeSeconds);
        _reticleSize.Value = ReticleSizeToSlider(_working.ReticleSize);
        _reticleThickness.Value = ReticleThicknessToSlider(_working.ReticleThickness);
        _multistream.Value = _working.MultistreamAudio;
        _master.Value = _working.MasterVolume;
        _music.Value = _working.MusicVolume;
        _sfx.Value = _working.SfxVolume;
        _bindingAction = null;
        CloseAllDropdowns();
        _showMultistreamHelp = false;
        _applyFeedbackText = string.Empty;
        _applyFeedbackTimer = 0f;
        _applyFeedbackIsError = false;
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
        UpdateTabTextures();
        _log.Info($"Options tab selected: {tab}");
    }

    private void UpdateTabTextures()
    {
        // Reset all tabs to normal textures
        _tabVideo.Texture = _assets.LoadTexture("textures/menu/buttons/Video.png");
        _tabAudio.Texture = _assets.LoadTexture("textures/menu/buttons/Audio.png");
        _tabControls.Texture = _assets.LoadTexture("textures/menu/buttons/Controls.png");
        _tabPacks.Texture = _assets.LoadTexture("textures/menu/buttons/packs.png");

        // Set selected tab to selected texture
        switch (_tab)
        {
            case Tab.Video:
                _tabVideo.Texture = _videoSelectedTex;
                break;
            case Tab.Audio:
                _tabAudio.Texture = _audioSelectedTex;
                break;
            case Tab.Controls:
                _tabControls.Texture = _controlsSelectedTex;
                break;
            case Tab.Packs:
                _tabPacks.Texture = _packsSelectedTex;
                break;
        }
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



