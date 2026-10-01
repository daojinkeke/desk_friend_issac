using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IssacPet
{
  // 方向：0=下 1=上 2=左 3=右
  internal enum Dir { Down, Up, Left, Right }

  internal static class Program
  {
    private const double Scale = 1.5;        // 32px 原始帧放大 1.5 倍
    private const double CellW = 32 * Scale; // 48
    private const double CellH = 42 * Scale; // 63（头+身合成后的固定高度）
    private const double SpeedControl = 10;  // 操控最大速度（像素/帧，66ms）
    private const double SpeedAuto = SpeedControl / 4;   // 自动游走最大速度
                                                         // 惯性参数（模仿以撒：速度向目标速度指数逼近，非瞬间启停）
    private const double AccelK = 0.4;      // 有输入时每 tick 向目标速度逼近的比例
    private const double FricK = 0.20;       // 无输入时摩擦衰减比例
    private const double VelSnap = 0.08;     // 低于此速度直接归零，防止无限滑动
    private const double MoveAnimEps = 0.35; // 速度超过此值才播行走动画

    // ---- 拖动甩出 + 重力物理 ----
    private const int TickMs = 33;                  // 定时器节拍（毫秒），速度换算用
    private const int FlingWindowMs = 100;          // 取松手前这段时间的鼠标位移估算甩出速度
    private const double FlingMinSpeed = 40;        // DIP/tick超过才甩得出去
    private const double FlingMaxSpeed = 55;        // 甩出初速上限（≈1667px/s）
    private const double Gravity = 1.0;             // 飞行重力加速度（DIP/tick²）
    private const double MaxFallSpeed = 40;         // 下坠速度上限
    private const double GroundFricK = 0.08;        // 落地滑行摩擦（比普通游走摩擦小，滑得更远）
    private const double WallHurtSpeed = 12;        // 撞墙/天花板速度达到此值 → 受伤
    private const double FloorHurtSpeed = 34;       // 砸地速度达到此值 → 受伤（轻落只滑行）
    private const double WallBounce = 0.35;         // 低速撞墙反弹系数（保留少量反向速度）
    private const double FloorBounceSpeed = 22;     // 空中撞地速度≥此值则弹起（低于则着地站立/滑行）
    private static bool _thrown;                    // 甩出飞行/滑行中（TickThrown 接管移动）
    private static bool _thrownGrounded;            // 已落地，只剩水平摩擦滑行

    // ---- 重力模式（右键菜单切换）：持续受重力、窗口上沿可站立、W 跳跃 ----
    private static bool _gravityMode;               // 当前是否处于重力模式
    private static bool _gGrounded;                 // 重力模式下是否着地（地面或窗口平台）
    private static bool _jumpLatch;                 // W 键边沿锁存（按下瞬间只跳一次）
    private const double JumpSpeed = 12;            // 跳跃初速（<FloorHurtSpeed：正常跳跃落地不受伤）
    private const double AirControlK = 0.15;        // 空中水平操控逼近比例（无按键时保留惯性）

    private static double _tvx, _tvy;        // 目标速度（输入期望），_vx/_vy 为实际速度
    private static double _posX, _posY;      // 浮点内部位置（子像素累积，渲染时才吸附物理像素）

    private static Window _win;
    private static Grid _root;              // 窗口根容器（整体缩放作用点）
    private const double ZoomFactor = 1.1;  // 右键菜单每档缩放：变大 ×1.1（+10%），变小 ÷1.1
    private static double _zoom = 1.0;      // 当前整体缩放（无上下限，初始 1=原始大小）

    // 角色实际不透明像素的包围盒（源像素坐标，相对窗口左上角；R/B 为外侧边界）。
    // 精灵格四周带透明留白，碰撞/站位以内容为准，避免放大后留白被等比放大导致脚边悬空、头顶留空
    // 注：用普通 struct（字段 readonly 即可），兼容系统自带 .NET Framework 的 C# 5 编译器 csc
    private struct ContentBounds
    {
      public readonly double L, T, R, B;
      public ContentBounds(double l, double t, double r, double b) { L = l; T = t; R = r; B = b; }
    }
    private static ContentBounds _cbNormal; // 常规：头帧(y0)+身帧(y10) 合成的 32x42 格
    private static ContentBounds _cbPose;   // 全身帧（抓取/受伤/退出）：32x64 帧放于 y=-1
    private static ContentBounds _cbThumb;  // 点赞：48x64 帧放于 y=-1
    private static ContentBounds _cbSleep;  // 睡眠：64x32 横帧放于 y=10
    // 碰撞底边额外下沉量（源像素）：内容盒 B 取的是全部行走帧脚底并集（走路最低帧），
    // 站姿实际脚底更高，贴边时视觉上会留缝；底边统一下沉此值，让站姿脚也贴到屏幕底边
    private const double FootSink = -2.6;
    private static Grid _walker;             // 头/身两层合成（待机与行走共用，保证清晰度一致）
    private static Image _bodyImage, _headImage, _dragImage, _thumbImage;
    private static double _dpi = 1.0;        // 物理像素/DIP，用于窗口坐标吸附（防亚像素虚影）

    private static CroppedBitmap[][] _bodyFrames; // [方向][帧]
    private static CroppedBitmap[] _headFrame;    // [方向] 每方向仅 1 帧（静态）
    private static CroppedBitmap[] _headAnim;     // [方向] 眨眼第 2 帧（方向键按住时播放）
    private static CroppedBitmap _bodyHold;       // 双臂上举身体帧（走向光标后举起光标的姿势）
    private static CroppedBitmap _headHold;       // 双臂上举头部帧（举光标时显示）
    //全身帧
    private static CroppedBitmap _dragpose;       // 抓取动作
    private static BitmapSource[] _hurtFrames;  // 受伤闪烁帧（32x64）：[0]=染红姿势帧 [1]=null（整拍消失）
    private static CroppedBitmap _exitpose;       // 退出游戏动作
    private static CroppedBitmap _sleeppose;       // 睡眠动作
    private static readonly Random Rnd = new Random();
    private static double _vx, _vy;
    private static bool _dragging;
    private static bool _dragPose;            // 鼠标真正抓住拖动中（越过点击阈值）：显示全身抓取帧
    private static bool _exitPosing;          // 右键菜单打开中：显示 _exitpose 并冻结移动
    private static Image _sleepImage;

    // 睡眠：游走累计约 120s / 举光标累计约 60s 自行入睡；睡约 5 分钟；鼠标点击打断
    private static bool _sleeping;
    private static int _sleepMs, _sleepDurationMs;                       // 本次睡眠已过/总时长
    private static int _awakeMs;
    private static int _awakeNeedMs = Rnd.Next(SleepAwakeMinMs, SleepAwakeMaxMs);
    private static int _holdMs;
    private static int _holdNeedMs = Rnd.Next(SleepHoldMinMs, SleepHoldMaxMs);
    private const int SleepAwakeMinMs = 100000, SleepAwakeMaxMs = 140000; // 100~140s（约120s）
    private const int SleepHoldMinMs = 45000, SleepHoldMaxMs = 75000;     // 45~75s（约60s）
    private const int SleepDurationMinMs = 60000, SleepDurationMaxMs = 300000; // 60~240s

    // 受伤闪烁（双击触发）：单个姿势帧与空帧交替，共 HurtFrameCount 个节拍，期间原地冻结
    private static bool _hurtPlaying;
    private static int _hurtFrame, _hurtTick; // _hurtFrame=已播放节拍数；显示帧取 _hurtFrame % 2
    private const int HurtFrameCount = 10;   // 闪烁节拍数（偶数：显示/消失各一半）
    private const int HurtDelayTicks = 2;    // 每节拍 tick 数（×33ms=66ms）

    // 点赞动画（单击触发）
    private static CroppedBitmap[] _thumbupFrames;
    private static bool _thumbupPlaying;
    private static int _thumbupFrame, _thumbupTick;
    private static readonly int[] ThumbupTicks = { 3, 12 };
    private const double ThumbWinExtra = 6 * Scale; // 播放时窗口向右加宽（36 源格=54DIP），容下探出的手势

    // 自动游走状态
    private static bool _walking = true;
    private static int _stateMs, _stateDuration, _animTick, _animFrame;

    // 朝向：身体与头部可独立
    private static Dir _bodyFacing = Dir.Down;
    private static Dir _headFacing = Dir.Down;
    private static int _headAnimTick, _headAnimFrame;
    private static bool _headAnimating;

    // 光标互动（游走模式下鼠标久置不动 → 走过去举起光标）
    private static bool _cursorAction;      // 正在接近或已举起（暂停普通游走调度）
    private static bool _approaching;       // 走向光标途中
    private static bool _holdingCursor;     // 已到位，保持举姿
    private static int _cursorIdleMs;       // 鼠标静止计时
    private static int _lastCursorPX = -1, _lastCursorPY = -1;
    private static double _targetX, _targetY;

    // 操控模式无输入超时
    private static int _controlIdleMs;

    // 操控模式：左键点击后开启
    private static bool _control;
    private static readonly List<Dir> _bodyKeys = new List<Dir>(); // WASD，栈顶=最后按下
    private static readonly List<Dir> _headKeys = new List<Dir>(); // 方向键

    // 虚拟键码（与 Dir 枚举顺序对应：Down Up Left Right）
    private static readonly int[] BodyVk = { 0x53, 0x57, 0x41, 0x44 }; // S W A D
    private static readonly int[] HeadVk = { 0x28, 0x26, 0x25, 0x27 }; // ↓ ↑ ← →
    private const int VkEscape = 0x1B;

    // ---- 低级键盘钩子：操控期间吞掉 WASD/方向键/Esc，按键不会发给任何窗口（不会打字/影响游戏）----
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private static IntPtr _kbHook = IntPtr.Zero;
    private static bool _escLatch; // 退出操控后继续吞掉本次 Esc 的自动重复，直到松手
    private static readonly LowLevelKeyboardProc _hookProc = KeyboardHookCallback; // 保持委托存活防 GC

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc cb, IntPtr hMod, uint tid);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string name);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInterop p);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointInterop { public int X, Y; }

    // ---- 窗口平台：被甩出时，打开的文件夹/应用窗口上沿可作单向站立平台 ----
    private static IntPtr _selfHwnd = IntPtr.Zero;   // 自身窗口句柄（枚举时排除）
    private static IntPtr _standingWindow = IntPtr.Zero; // 当前正站在哪个窗口上（Zero=屏幕地面/空中）
    private const int PlatformTopInsetPx = 6;        // 窗口物理顶边内缩（DWM 不可见阴影边框），让脚视觉贴住标题栏上沿
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr lParam);
    private static readonly EnumWindowsProc _enumWindowsProc = EnumWindowsCallback; // 静态引用防 GC
    private sealed class PlatformHit { public IntPtr H; public double Top, Left, Right; }
    private static readonly List<PlatformHit> _platformHits = new List<PlatformHit>();
    private static double _platPrevFoot, _platNewFoot, _platLeft, _platRight;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out WinRect r);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr h, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder buf, int maxCount);

    private static void InstallKeyboardHook()
    {
      using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
      using (System.Diagnostics.ProcessModule m = p.MainModule)
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(m.ModuleName), 0);
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
      if (nCode >= 0 && _control)
      {
        int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
        bool down = (int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN;
        bool up = (int)wParam == WM_KEYUP || (int)wParam == WM_SYSKEYUP;

        if (vk == VkEscape)
        {
          if (down && !_escLatch) { _escLatch = true; ExitControl(); }
          if (up) _escLatch = false;
          return (IntPtr)1; // 吞掉，连 Esc 也不外泄（含松手前的自动重复）
        }
        int bi = Array.IndexOf(BodyVk, vk);
        int hi = Array.IndexOf(HeadVk, vk);
        if (bi >= 0 || hi >= 0)
        {
          if (down)
          {
            _controlIdleMs = 0; // 任意受控键按下，刷新无输入计时
            if (bi >= 0 && !_bodyKeys.Contains((Dir)bi)) _bodyKeys.Add((Dir)bi);
            if (hi >= 0 && !_headKeys.Contains((Dir)hi)) _headKeys.Add((Dir)hi);
          }
          else if (up)
          {
            if (bi >= 0) _bodyKeys.Remove((Dir)bi);
            if (hi >= 0) _headKeys.Remove((Dir)hi);
          }
          return (IntPtr)1; // 吞掉按键：焦点窗口收不到，不会打字
        }
      }
      return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    [STAThread]
    private static void Main()
    {
      string logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
      AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs args)
      {
        File.AppendAllText(logPath, "AppDomain: " + args.ExceptionObject + Environment.NewLine);
      };

      Application app = new Application();
      app.DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs args)
      {
        File.AppendAllText(logPath, DateTime.Now + " Dispatcher: " + args.Exception + Environment.NewLine);
        args.Handled = true;
      };

      BitmapSource sheet = LoadEmbeddedImage("character_001_isaac.png");
      BuildWalkFrames(sheet);

      _bodyImage = new Image { Width = CellW, Height = CellW };
      _headImage = new Image { Width = CellW, Height = CellW };
      // 全身姿势帧图层（抓取/受伤共用）：32x64 源帧  = 48x96DIP，裁切原点与头部帧原点重合
      // Top 上移 1 源像素：64 高帧脚底（最大 y=42）对齐常规合成脚底（63DIP），不被窗口底边裁切
      _dragImage = new Image { Width = 32 * Scale, Height = 64 * Scale, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
      Canvas.SetTop(_dragImage, -1 * Scale);
      RenderOptions.SetBitmapScalingMode(_bodyImage, BitmapScalingMode.NearestNeighbor);
      RenderOptions.SetBitmapScalingMode(_headImage, BitmapScalingMode.NearestNeighbor);
      RenderOptions.SetBitmapScalingMode(_dragImage, BitmapScalingMode.NearestNeighbor);
      // 点赞专用宽图层：48x64 源帧  = 72x96DIP（手势探出 32 格外，不能复用 48 宽的 _dragImage，
      // 否则 Stretch.Fill 会横向压扁）；原点与头部帧重合，Top 同样上移 1 源像素
      _thumbImage = new Image { Width = 48 * Scale, Height = 64 * Scale, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
      Canvas.SetTop(_thumbImage, -1 * Scale);
      RenderOptions.SetBitmapScalingMode(_thumbImage, BitmapScalingMode.NearestNeighbor);

      // 身体层距顶部 15px（原始帧偏移 +10px），头部层置顶（头盖身）
      _walker = new Grid { Width = CellW, Height = CellH };
      Canvas bodyLayer = new Canvas { Width = CellW, Height = CellH };
      Canvas.SetTop(_bodyImage, 10 * Scale);
      bodyLayer.Children.Add(_bodyImage);
      // 睡眠横帧：64x32 源帧  = 96x48DIP；原点与身体帧重合（同层同 Top），默认隐藏
      _sleepImage = new Image { Source = _sleeppose, Width = 64 * Scale, Height = 32 * Scale, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
      Canvas.SetTop(_sleepImage, 10 * Scale);
      RenderOptions.SetBitmapScalingMode(_sleepImage, BitmapScalingMode.NearestNeighbor);
      bodyLayer.Children.Add(_sleepImage);
      Canvas headLayer = new Canvas { Width = CellW, Height = CellH };
      headLayer.Children.Add(_headImage);
      headLayer.Children.Add(_dragImage);
      headLayer.Children.Add(_thumbImage);
      _walker.Children.Add(bodyLayer);
      _walker.Children.Add(headLayer);

      Grid root = new Grid { Width = CellW, Height = CellH, Focusable = true };
      _root = root;
      root.Children.Add(_walker);

      _win = new Window
      {
        Content = root,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = null, // 无背景：精灵留白处点击穿透（内容贴边时窗口透明条伸出工作区也不挡任务栏/桌面）
        Topmost = true,
        ShowInTaskbar = false,
        ResizeMode = ResizeMode.NoResize,
        Width = CellW,
        Height = CellH
      };

      Rect area = ActiveArea;
      // 初始水平按角色实际内容居中，垂直让实际脚底贴工作区底边（任务栏上沿）
      ContentBounds cb0 = _cbNormal;
      _posX = area.Left + (area.Width - (cb0.R - cb0.L) * Scale) / 2 - cb0.L * Scale;
      _posY = area.Bottom - (cb0.B + FootSink) * Scale;
      _win.Left = _posX;
      _win.Top = _posY;

      // 右键菜单：变大/变小、重力模式切换（可来回切）、退出
      MenuItem biggerItem = new MenuItem { Header = "bigger pills" };
      biggerItem.Click += delegate { PlayEmbeddedSound("up.mp3"); Zoom(ZoomFactor); };
      MenuItem smallerItem = new MenuItem { Header = "smaller pills" };
      smallerItem.Click += delegate { PlayEmbeddedSound("up.mp3"); Zoom(1.0 / ZoomFactor); };
      MenuItem gravityItem = new MenuItem { Header = "gravity pills?" };
      gravityItem.Click += delegate { PlayEmbeddedSound("up.mp3"); ToggleGravityMode(); gravityItem.Header = _gravityMode ? "i found a strange pills" : "gravity pills"; };
      MenuItem exitItem = new MenuItem { Header = "do you truly want me to die?" };
      exitItem.Click += delegate { PlayExitThenShutdown(app); };
      root.ContextMenu = new ContextMenu();
      root.ContextMenu.Items.Add(biggerItem);
      root.ContextMenu.Items.Add(smallerItem);
      root.ContextMenu.Items.Add(gravityItem);
      root.ContextMenu.Items.Add(exitItem);
      // 菜单打开：中断举光标、冻结移动、切退出姿势；关闭：恢复（游走模式停顿片刻再走）
      root.ContextMenu.Opened += delegate
      {
        PlayEmbeddedSound("mom" + Rnd.Next(1, 4) + ".mp3");
        gravityItem.Header = _gravityMode ? "i found a strange pills" : "gravity pills";
        CancelCursorAction();
        _thrown = false; // 飞行中/站在窗口上打开菜单：撤销甩出，角色定住保持退出姿势
        _thrownGrounded = false;
        _standingWindow = IntPtr.Zero;
        _gGrounded = false; // 关闭菜单后从当前位置重新探测支撑（脚下窗口已关则掉落）
        _exitPosing = true;
        _tvx = _tvy = _vx = _vy = 0;
        ApplyFrame();
      };
      root.ContextMenu.Closed += delegate
      {
        _exitPosing = false;
        ApplyFrame();
        // 仍在睡眠或键盘操控中时不额外调度（睡眠 Tick 自身管理，操控由 TickControl 管理）
        if (!_sleeping && !_control) StartIdleThenWalk(500);
      };

      // 左键：手动拖拽状态机（不用 DragMove——分层窗口会有启动跳变导致点击误判）
      // 按下即暂停自动游走；超阈值才真正拖窗；拖拽以按下瞬间的鼠标物理坐标为基准
      Point grabPoint = new Point();         // 抓取点相对窗口（DIP，仅用于点击阈值判定）
      PointInterop grabCursor = new PointInterop(); // 按下瞬间的鼠标物理坐标
      double grabPosX = 0, grabPosY = 0;     // 按下瞬间的窗口浮点位置
      bool mouseDown = false;
      bool pastDrag = false;
      bool wokeFromSleep = false; // 本次按下是否用于唤醒睡眠（唤醒点击不播点赞动画）
      const double DragThreshold = 5.0; // 逻辑像素，天然适配 DPI 缩放
      // 拖动轨迹采样（物理像素 + TickCount 时间戳），松手时取最近 FlingWindowMs 的端点差估甩出速度
      List<FlingSample> flingSamples = new List<FlingSample>();

      root.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
      {
        wokeFromSleep = _sleeping;
        if (_sleeping) WakeUp(); // 点击（含拖拽/双击）立即打断睡眠，后续按正常点击流程进操控
        mouseDown = true;
        pastDrag = false;
        flingSamples.Clear();
        _thrown = false; // 飞行途中/站在窗口上一把抓住：取消甩出（TickThrown 让出控制权）
        _thrownGrounded = false;
        _standingWindow = IntPtr.Zero;
        _dragging = true; // 按下期间冻结自动游走，防止窗口在静止鼠标下滑走造成误判
        CancelCursorAction(); // 抓住/点击桌宠时中断"举光标"行为
        grabPoint = e.GetPosition(_win);
        GetCursorPos(out grabCursor);
        grabPosX = _posX;
        grabPosY = _posY;
        root.CaptureMouse();
        // 被抓住瞬间立即冻结：目标与实际速度都清零，不带惯性
        _tvx = _tvy = _vx = _vy = 0;
        if (!_control)
        {
          SetWalking(false);
          _bodyFacing = Dir.Down;
          _headFacing = Dir.Down;
          _animFrame = 0;
          ApplyFrame();
        }
        // 双击（系统双击时限内，ClickCount 由 WPF 维护）：播放受伤动作
        if (e.ClickCount == 2) StartHurt();
      };
      root.MouseMove += delegate (object s, MouseEventArgs e)
      {
        if (!mouseDown) return;
        Point pos = e.GetPosition(_win);
        if (!pastDrag &&
                  (Math.Abs(pos.X - grabPoint.X) > DragThreshold || Math.Abs(pos.Y - grabPoint.Y) > DragThreshold))
        {
          pastDrag = true;
          // 真正抓住开始拖动：切换为全身抓取帧（点击不越阈值则不触发）
          _dragPose = true;
          ApplyFrame();
        }
        if (pastDrag)
        {
          // 全部用物理坐标计算再换算回 DIP：窗口原点 = 鼠标当前位置 - 抓取偏移
          PointInterop cur;
          GetCursorPos(out cur);
          _posX = grabPosX + (cur.X - grabCursor.X) / _dpi;
          _posY = grabPosY + (cur.Y - grabCursor.Y) / _dpi;
          ClampIntoScreen(_cbNormal); // 移动范围与键盘移动统一（按常规主体盒，而非显示中的抓姿盒）
          SyncWindowPos();
          // 记录轨迹采样并淘汰窗口外旧样本（至少保留 2 个用于端点差分）
          int nowMs = Environment.TickCount;
          flingSamples.Add(new FlingSample { T = nowMs, X = cur.X, Y = cur.Y });
          while (flingSamples.Count > 2 && nowMs - flingSamples[0].T > FlingWindowMs)
            flingSamples.RemoveAt(0);
        }
      };
      root.MouseLeftButtonUp += delegate
      {
        if (!mouseDown) return;
        mouseDown = false;
        _dragging = false;
        _dragPose = false;
        root.ReleaseMouseCapture();
        bool wasDrag = pastDrag;
        pastDrag = false;
        ClampIntoScreen();
        if (!wasDrag)
        {
          EnterControl();                      // 原地松手 = 点击 → 进入键盘操控
          if (!wokeFromSleep && Rnd.Next(5) == 0) StartThumbup();  // 唤醒睡眠的那一下只唤醒；点赞动画单击 1/5 概率触发
        }
        else
        {
          // 快速甩动松手：用最近 FlingWindowMs 的鼠标端点差估算释放速度（物理像素/ms → DIP/tick）
          double flingVx = 0, flingVy = 0;
          if (flingSamples.Count >= 2)
          {
            int nowMs = Environment.TickCount;
            while (flingSamples.Count > 2 && nowMs - flingSamples[0].T > FlingWindowMs)
              flingSamples.RemoveAt(0);
            FlingSample s0 = flingSamples[0];
            FlingSample s1 = flingSamples[flingSamples.Count - 1];
            int dt = s1.T - s0.T; // TickCount 回绕/同毫秒时放弃，按未甩出处理
            // 最新采样也必须在窗口内（停住后才松手不算甩动），否则陈旧端点会误判成高速
            if (dt > 0 && nowMs - s1.T <= FlingWindowMs)
            {
              flingVx = (double)(s1.X - s0.X) / dt / _dpi * TickMs;
              flingVy = (double)(s1.Y - s0.Y) / dt / _dpi * TickMs;
              double mag = Math.Sqrt(flingVx * flingVx + flingVy * flingVy);
              if (mag > FlingMaxSpeed) { double k = FlingMaxSpeed / mag; flingVx *= k; flingVy *= k; }
            }
            flingSamples.Clear();
          }
          // 超过阈值才甩出；否则维持原逻辑（操控中不打断，游走模式停顿后继续走）
          if (Math.Sqrt(flingVx * flingVx + flingVy * flingVy) >= FlingMinSpeed)
            StartFling(flingVx, flingVy);
          else
          {
            // 重力模式：从释放位置重新探测支撑（拖到空中松手会掉下，落在窗口/地面则一站）
            if (_gravityMode) { _gGrounded = false; _standingWindow = IntPtr.Zero; _vy = 0; }
            if (!_control) StartIdleThenWalk(800);
          }
        }
        ApplyFrame();                          // 恢复常规头/身两层（EnterControl 内部也会刷一次）
      };

      // 窗口句柄建立后取 DPI 缩放（150% 屏 = 1.5），用于坐标物理像素吸附
      _win.SourceInitialized += delegate
      {
        PresentationSource ps = PresentationSource.FromVisual(_win);
        if (ps != null) _dpi = ps.CompositionTarget.TransformToDevice.M11;
        _selfHwnd = new System.Windows.Interop.WindowInteropHelper(_win).Handle;
      };

      // 全局低级键盘钩子：操控期间拦截 WASD/方向键/Esc
      InstallKeyboardHook();
      AppDomain.CurrentDomain.ProcessExit += delegate { if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook); };

      // 焦点丢失（点了桌面/其他窗口）→ 退出操控，恢复自动游走，同时释放按键拦截
      _win.Deactivated += delegate { if (_control) ExitControl(); };

      InitSfx();
      StartWalking();

      DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
      timer.Tick += Tick;
      timer.Start();

      app.Run(_win);
    }

    #region 操控模式

    // 双击：播放受伤动作（视觉由 ApplyFrame 的受伤分支统一处理）
    private static void StartHurt()
    {
      _thumbupPlaying = false; // 双击的受伤动画覆盖并终止第一次点击的点赞动画
      SetThumbWindow(false);
      _hurtPlaying = true;
      _hurtFrame = 0;
      _hurtTick = 0;

      PlayHurtSound();
      ApplyFrame();
    }

    // 受伤音效（wav）：启动时 3 个 wav 已整包预读进内存；触发即用 SoundPlayer 后台线程
    // PlaySync——即时发声、可重叠、播完自释放。WAV 解码同步且无 MediaPlayer 的异步加载抢跑问题
    private static byte[][] _hurtWav = new byte[3][];

    private static void PlayHurtSound()
    {
      byte[] data = _hurtWav[Rnd.Next(0, 3)];
      if (data == null) return;
      ThreadPool.QueueUserWorkItem(delegate
      {
        using (MemoryStream ms = new MemoryStream(data))
        {
          SoundPlayer player = new SoundPlayer(ms);
          player.PlaySync();
          player.Dispose();
        }
      });
    }

    // mp3 播放器声道：实测 MediaPlayer 必须 Open 后立即 Play（指令排队）；且“自然播完后
    // Position=0 + Play”重播可靠，而“只 Open 等就绪再播 / 播放中途重启”会丢指令。
    // 故启动时静音(Volume=0)暖机播完，Ready 后才能被触发重播
    private sealed class SfxVoice
    {
      public MediaPlayer Player;
      public string File;
      public bool Ready; // 静音预热已自然播完，可随时从头重播
    }

    private const int VoicesPerSfx = 2; // 每种 mp3 2 个声道；都在播时走新建播放器兜底
    private static readonly Dictionary<string, SfxVoice[]> _sfxPool = new Dictionary<string, SfxVoice[]>();
    private static readonly List<MediaPlayer> _fallbackPlayers = new List<MediaPlayer>();
    private static readonly List<string> _sfxFiles = new List<string>();

    private static readonly string[] Mp3SfxNames =
    {
      "mom1.mp3", "mom2.mp3", "mom3.mp3",
      "mouse1.mp3", "mouse2.mp3",
      "up.mp3", "thumbs up.mp3", "thumbs down.mp3",
      "evil laugh.mp3"
    };

    private static void InitSfx()
    {
      // hurt wav：整包读入内存（不落地临时文件）
      for (int i = 0; i < 3; i++) _hurtWav[i] = ExtractBytes("hurt" + (i + 1) + ".wav");

      // mp3：错峰静音预热，每 200ms 暖一组（避免启动瞬间并发打开十几个解码器）
      Queue<string> pending = new Queue<string>(Mp3SfxNames);
      DispatcherTimer warmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
      warmTimer.Tick += delegate
      {
        if (pending.Count == 0) { warmTimer.Stop(); return; }
        string name = pending.Dequeue();
        string file = ExtractSfx(name);
        if (file == null) return;
        _sfxFiles.Add(file);
        SfxVoice[] voices = new SfxVoice[VoicesPerSfx];
        for (int v = 0; v < VoicesPerSfx; v++) voices[v] = CreateVoice(file);
        _sfxPool[name] = voices;
      };
      warmTimer.Start();

      Application.Current.Exit += delegate
      {
        foreach (SfxVoice[] voices in _sfxPool.Values)
          foreach (SfxVoice v in voices)
            try { v.Player.Close(); } catch { }
        lock (_fallbackPlayers)
          foreach (MediaPlayer p in _fallbackPlayers)
            try { p.Close(); } catch { }
        foreach (string file in _sfxFiles)
          try { File.Delete(file); } catch { }
      };
    }

    // 嵌入资源 → byte[]（hurt wav 用）；失败返回 null
    private static byte[] ExtractBytes(string logicalName)
    {
      Stream res = Assembly.GetEntryAssembly().GetManifestResourceStream(logicalName);
      if (res == null) return null;
      using (res)
      using (MemoryStream ms = new MemoryStream())
      {
        res.CopyTo(ms);
        return ms.ToArray();
      }
    }

    // 嵌入资源 → 临时文件（mp3 用，每种只提取一次）；失败返回 null
    private static string ExtractSfx(string logicalName)
    {
      Stream res = Assembly.GetEntryAssembly().GetManifestResourceStream(logicalName);
      if (res == null) return null;
      string safe = logicalName.Replace(' ', '_');
      string file = Path.Combine(Path.GetTempPath(),
        "issac_sfx_" + Guid.NewGuid().ToString("N") + "_" + safe);
      try
      {
        using (res)
        using (FileStream fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
          res.CopyTo(fs);
        return file;
      }
      catch
      {
        try { File.Delete(file); } catch { }
        return null;
      }
    }

    private static SfxVoice CreateVoice(string file)
    {
      SfxVoice v = new SfxVoice { File = file };
      MediaPlayer player = new MediaPlayer { Volume = 0 };
      v.Player = player;

      player.MediaEnded += delegate { v.Ready = true; };
      player.MediaFailed += delegate
      {
        // 解码器首次初始化偶发失败：400ms 后重新 Open + Play 暖机
        v.Ready = false;
        DispatcherTimer retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        retry.Tick += delegate
        {
          retry.Stop();
          player.Volume = 0;
          player.Open(new Uri(v.File, UriKind.Absolute));
          player.Play();
        };
        retry.Start();
      };
      player.Open(new Uri(file, UriKind.Absolute));
      player.Play(); // 关键：Play 紧跟 Open，静音暖机直到自然播完
      return v;
    }

    // 播放指定嵌入 mp3：取一个已暖机就绪的声道从头重播；
    // 声道都在播（或预热尚未完成）→ 新建播放器 Open+Play 立即兜底
    private static void PlayEmbeddedSound(string logicalName)
    {
      SfxVoice[] voices;
      if (_sfxPool.TryGetValue(logicalName, out voices))
      {
        foreach (SfxVoice v in voices)
        {
          if (v.Ready)
          {
            v.Ready = false;
            v.Player.Volume = 1;
            v.Player.Position = TimeSpan.Zero;
            v.Player.Play();
            return;
          }
        }
      }
      PlayFallback(logicalName, voices);
    }

    // 兜底：新建 MediaPlayer，Open 后立即 Play；播完/失败后关闭（临时文件退出时统一删）
    private static void PlayFallback(string logicalName, SfxVoice[] voices)
    {
      string file = voices != null ? voices[0].File : null;
      if (file == null)
      {
        file = ExtractSfx(logicalName);
        if (file == null) return;
        _sfxFiles.Add(file);
      }

      MediaPlayer player = new MediaPlayer { Volume = 1 };
      lock (_fallbackPlayers) _fallbackPlayers.Add(player);
      bool done = false;
      Action cleanup = delegate
      {
        if (done) return;
        done = true;
        lock (_fallbackPlayers) _fallbackPlayers.Remove(player);
        player.Close();
      };
      player.MediaEnded += delegate { cleanup(); };
      player.MediaFailed += delegate { cleanup(); };
      player.Open(new Uri(file, UriKind.Absolute));
      player.Play();
    }

    // 点击退出：人物立即消失（隐藏窗口但进程仍在），播放 evil laugh.mp3，
    // 声音自然播完后再关闭进程；播放失败或超过 8s（防解码器异常卡死）强制退出；
    // flag 保证 Shutdown 只执行一次
    private static void PlayExitThenShutdown(Application app)
    {
      _win.Hide(); // 人物先不见
      bool shuttingDown = false;
      Action doShutdown = delegate
      {
        if (shuttingDown) return;
        shuttingDown = true;
        app.Shutdown();
      };

      string file = null;
      SfxVoice[] voices;
      if (_sfxPool.TryGetValue("evil laugh.mp3", out voices)) file = voices[0].File;
      if (file == null) file = ExtractSfx("evil laugh.mp3"); // 预热尚未完成也不阻塞退出流程
      if (file == null) { doShutdown(); return; }

      MediaPlayer player = new MediaPlayer { Volume = 1 };
      player.MediaEnded += delegate { doShutdown(); };
      player.MediaFailed += delegate { doShutdown(); };
      player.Open(new Uri(file, UriKind.Absolute));
      player.Play();

      DispatcherTimer timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
      timeout.Tick += delegate { timeout.Stop(); doShutdown(); };
      timeout.Start();
    }

    // 每 tick 推进受伤动画；播完返回 false
    private static bool TickHurt()
    {
      if (!_hurtPlaying) return false;
      if (++_hurtTick >= HurtDelayTicks)
      {
        _hurtTick = 0;
        if (++_hurtFrame >= HurtFrameCount) _hurtPlaying = false;
        ApplyFrame();
      }
      return _hurtPlaying;
    }

    // 单击：播放点赞动画（2 张图，各 2 / 4 tick），移动不冻结
    private static void StartThumbup()
    {
      _thumbupPlaying = true;
      _thumbupFrame = 0;
      _thumbupTick = 0;
      SetThumbWindow(true);
      ClampIntoScreen(); // 加宽后若已贴屏幕右缘，左移收进工作区
      SyncWindowPos();
      ApplyFrame();
    }

    private static void TickThumbup()
    {
      if (!_thumbupPlaying) return;
      if (++_thumbupTick >= ThumbupTicks[_thumbupFrame])
      {
        _thumbupTick = 0;
        if (++_thumbupFrame >= _thumbupFrames.Length)
        {
          _thumbupPlaying = false;
          SetThumbWindow(false);
        }
        ApplyFrame();
      }
    }

    // 点赞帧 48 源格宽，播放期间窗口向右加宽 ThumbWinExtra（向左不动，人物原点不跳）
    // 宽高均随当前整体缩放 _zoom 放大
    private static void SetThumbWindow(bool wide)
    {
      _win.Width = (wide ? CellW + ThumbWinExtra : CellW) * _zoom;
      _win.Height = CellH * _zoom;
    }

    // 右键菜单：按 factor 整体缩放（bigger=×1.1 / smaller=÷1.1），不设上下限、可连续点击。
    // 缩放作用于根容器布局变换，并同步窗口尺寸；以角色脚底中心为锚重新定位，避免缩放瞬间跳位
    private static void Zoom(double factor)
    {
      ContentBounds cb = CurrentBounds;
      double centerX = _posX + (cb.L + cb.R) * 0.5 * Scale * _zoom; // 实际内容水平中心
      double bottomY = _posY + (cb.B + FootSink) * Scale * _zoom;  // 实际脚底（含下沉偏移）
      _zoom *= factor;
      _root.LayoutTransform = new ScaleTransform(_zoom, _zoom);
      SetThumbWindow(_thumbupPlaying); // 宽度按是否在播点赞统一设置，高度随缩放更新
      _posX = centerX - (cb.L + cb.R) * 0.5 * Scale * _zoom;
      _posY = bottomY - (cb.B + FootSink) * Scale * _zoom;
      ClampIntoScreen(); // 缩放后若超出屏幕边缘，收进工作区
      SyncWindowPos();
    }

    // 右键菜单：普通模式 ↔ 重力模式。
    // 进入：清空速度与支撑状态，角色从当前位置受重力落下（站地/窗口即撑住）；操控状态保留
    // 退出：清空重力状态，恢复普通全向游走
    private static void ToggleGravityMode()
    {
      _gravityMode = !_gravityMode;
      _jumpLatch = false;
      _gGrounded = false;
      _standingWindow = IntPtr.Zero;
      _vx = _vy = _tvx = _tvy = 0;
      CancelCursorAction(); // 重力模式不举光标；切回普通时也干净起步
      StartIdleThenWalk(_gravityMode ? 300 : 500);
      ApplyFrame();
    }

    private static void EnterControl()
    {
      CancelCursorAction();
      _control = true; // 置位后键盘钩子立即开始拦截受控按键
      _escLatch = false;
      _controlIdleMs = 0;
      _bodyKeys.Clear();
      _headKeys.Clear();
      _vx = _vy = _tvx = _tvy = 0;
      _bodyFacing = Dir.Down;
      _headFacing = Dir.Down;
      _headAnimating = false;
      _animFrame = 0;
      _animTick = 0;
      SetWalking(false);
      ApplyFrame();
      _win.Activate();
    }

    private static void ExitControl()
    {
      _control = false; // 先解除拦截，避免随后物理按键状态卡住
      _bodyKeys.Clear();
      _headKeys.Clear();
      _headAnimating = false;
      _headFacing = _bodyFacing;
      StartIdleThenWalk(500);
    }

    #endregion

    #region 甩出与重力

    // 高速松手：以释放速度进入甩出态，由 TickThrown 全权接管移动（退出操控/举光标/点赞，避免多系统同时积分）
    private static void StartFling(double vx, double vy)
    {
      if (_control)
      {
        _control = false; // 同 ExitControl：先解除按键拦截
        _bodyKeys.Clear();
        _headKeys.Clear();
        _headAnimating = false;
      }
      CancelCursorAction(); // 内部会清零速度并重排游走调度，随后再覆盖为甩出初速度
      if (_thumbupPlaying)
      {
        _thumbupPlaying = false;
        SetThumbWindow(false);
      }
      _thrown = true;
      _thrownGrounded = false;
      _standingWindow = IntPtr.Zero;
      _gGrounded = false; // 进入甩出即脱离重力模式支撑（否则受伤结束回 GravityStep 会被强制贴地=瞬移）
      _vx = vx;
      _vy = vy;
      _tvx = _tvy = 0;
      _walking = false;
      ClampIntoScreen();
      SyncWindowPos();
      UpdateBodyVisual(true, null); // 飞行中用行走帧，朝向跟随速度
      _headFacing = _bodyFacing;
    }

    // ---- 窗口平台查询 ----

    // EnumWindows 回调：筛出"有标题栏/可调边框的可见普通窗口"，且顶边落在本帧下落扫掠区间、水平与角色重叠
    private static bool EnumWindowsCallback(IntPtr h, IntPtr lParam)
    {
      if (h == _selfHwnd || !IsWindowVisible(h)) return true;
      int style = GetWindowLong(h, GWL_STYLE);
      if ((style & (int)(WS_CAPTION | WS_THICKFRAME)) == 0) return true;
      // 排除任务栏、桌面层
      System.Text.StringBuilder cn = new System.Text.StringBuilder(64);
      GetClassName(h, cn, cn.Capacity);
      string cls = cn.ToString();
      if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTray" || cls == "Progman" || cls == "WorkerW") return true;

      WinRect r;
      if (!GetWindowRect(h, out r)) return true;
      if (r.Right - r.Left < 90 || r.Bottom - r.Top < 45) return true; // 过滤小工具条/气泡
      // 物理像素 → DIP；顶边内缩 DWM 不可见阴影，视觉上脚贴标题栏上沿
      double top = (r.Top + PlatformTopInsetPx) / _dpi;
      double left = r.Left / _dpi, right = r.Right / _dpi;
      if (top < _platPrevFoot - 1 || top > _platNewFoot) return true; // 仅本帧下落扫过的顶边
      if (_platRight <= left || _platLeft >= right) return true;      // 水平需有重叠
      _platformHits.Add(new PlatformHit { H = h, Top = top, Left = left, Right = right });
      return true;
    }

    // 下落时找扫掠区间内最高（top 最小）的窗口平台；找到返回句柄与站立顶边
    private static IntPtr FindPlatformBelow(double prevFoot, double newFoot, double leftX, double rightX, out double topDip)
    {
      _platPrevFoot = prevFoot; _platNewFoot = newFoot; _platLeft = leftX; _platRight = rightX;
      _platformHits.Clear();
      EnumWindows(_enumWindowsProc, IntPtr.Zero);
      IntPtr best = IntPtr.Zero;
      double bestTop = double.MaxValue;
      foreach (PlatformHit hit in _platformHits)
        if (hit.Top < bestTop) { bestTop = hit.Top; best = hit.H; }
      _platformHits.Clear();
      topDip = bestTop;
      return best;
    }

    // 已站在窗口上时的有效性：窗口仍可见未最小化、顶边仍在屏幕内、角色水平仍压在窗口范围内；
    // 有效则输出当前顶边/左右边（窗口被拖动时顶边随之变化，角色跟随）
    private static bool TryGetStandPlatform(IntPtr h, double leftX, double rightX, double screenBottom, out double topDip)
    {
      topDip = 0;
      if (h == IntPtr.Zero || !IsWindow(h) || !IsWindowVisible(h) || IsIconic(h)) return false;
      WinRect r;
      if (!GetWindowRect(h, out r)) return false;
      if (r.Right - r.Left < 90 || r.Bottom - r.Top < 45) return false;
      double top = (r.Top + PlatformTopInsetPx) / _dpi;
      double left = r.Left / _dpi, right = r.Right / _dpi;
      if (rightX <= left || leftX >= right) return false; // 水平走出窗口边缘 → 掉落
      if (top > screenBottom) return false;               // 窗口被拖出屏幕底边 → 掉落
      topDip = top;
      return true;
    }

    // 甩出物理推进：重力下坠；分轴位移+碰撞推出（只处理碰撞轴速度）；高速撞墙/砸地/砸窗口触发受伤
    private static void TickThrown()
    {
      Rect a = ActiveArea;
      ContentBounds cb = CurrentBounds;
      double edgeL = cb.L * Scale * _zoom, edgeR = cb.R * Scale * _zoom;
      double edgeT = cb.T * Scale * _zoom, edgeB = (cb.B + FootSink) * Scale * _zoom;

      if (!_thrownGrounded) _vy = Math.Min(_vy + Gravity, MaxFallSpeed);

      // —— X 轴：先位移，再贴墙推出并解算水平速度 ——
      _posX += _vx;
      if (_vx < 0 && _posX + edgeL <= a.Left)
      {
        _posX = a.Left - edgeL;
        if (Math.Abs(_vx) >= WallHurtSpeed)
        {
          StartHurt();              // 高速撞墙：播受伤动画/音效，但物理不中断
          _vx = -_vx * WallBounce;  // 同样弹回（弹后速度低于阈值，下 tick 离墙不会连击）
        }
        else _vx = _thrownGrounded ? 0 : -_vx * WallBounce; // 落地滑行撞墙直接停；空中轻碰弹回
      }
      else if (_vx > 0 && _posX + edgeR >= a.Right)
      {
        _posX = a.Right - edgeR;
        if (Math.Abs(_vx) >= WallHurtSpeed)
        {
          StartHurt();
          _vx = -_vx * WallBounce;
        }
        else _vx = _thrownGrounded ? 0 : -_vx * WallBounce;
      }

      // —— Y 轴：天花板 / 屏幕地面 / 窗口平台（单向：仅从上方落下时站住）——
      double prevFoot = _posY + edgeB; // 位移前脚底，用于下落扫掠检测，防高速穿透平台
      _posY += _vy;
      if (_vy < 0 && _posY + edgeT <= a.Top)
      {
        _posY = a.Top - edgeT;
        if (Math.Abs(_vy) >= WallHurtSpeed) StartHurt(); // 高速撞顶受伤，但物理不中断
        _vy = -_vy * WallBounce;                          // 天花板反弹（同左右墙）
      }
      else if (_vy >= 0)
      {
        // 已站在窗口上：窗口移动则跟随，关闭/最小化/走出边缘则重新进入自由落体
        bool onWindow = _thrownGrounded && _standingWindow != IntPtr.Zero;
        if (onWindow)
        {
          double pTop;
          if (TryGetStandPlatform(_standingWindow, _posX + edgeL, _posX + edgeR, a.Bottom, out pTop))
          {
            _posY = pTop - edgeB;
            _vy = 0;
          }
          else
          {
            _standingWindow = IntPtr.Zero;
            _thrownGrounded = false;
            onWindow = false; // 本 tick 起下坠，下 tick 开始重新寻找平台
          }
        }
        if (!onWindow)
        {
          double newFoot = _posY + edgeB;
          if (newFoot >= a.Bottom)
          {
            // 屏幕地面（任务栏上沿）：空中高速砸地受伤并弹起；低速/已落地则着地滑行
            _posY = a.Bottom - edgeB;
            if (!_thrownGrounded && Math.Abs(_vy) >= FloorHurtSpeed) StartHurt();
            if (!_thrownGrounded && Math.Abs(_vy) >= FloorBounceSpeed)
              _vy = -_vy * WallBounce;
            else
            {
              _vy = 0;
              _thrownGrounded = true;
              _standingWindow = IntPtr.Zero;
            }
          }
          else if (_vy > 0)
          {
            // 自由下落：扫掠区间内有窗口顶边则落在最高的那个上
            double pTop;
            IntPtr hit = FindPlatformBelow(prevFoot, newFoot, _posX + edgeL, _posX + edgeR, out pTop);
            if (hit != IntPtr.Zero)
            {
              _posY = pTop - edgeB;
              if (Math.Abs(_vy) >= FloorHurtSpeed) StartHurt();
              if (Math.Abs(_vy) >= FloorBounceSpeed)
                _vy = -_vy * WallBounce; // 窗口平台也弹起（保持空中）
              else
              {
                _vy = 0;
                _thrownGrounded = true;
                _standingWindow = hit;
              }
            }
          }
        }
      }

      // 落地后：水平摩擦滑行。滑到屏幕地面即收尾回游走；滑停在窗口顶上则原地待机，
      // 继续由 TickThrown 托管（窗口关闭/被拖走时自然掉落，落地后再恢复游走）
      if (_thrownGrounded)
      {
        _vx = Approach(_vx, 0, GroundFricK);
        if (_vx == 0 && _standingWindow == IntPtr.Zero) { EndFling(false); return; }
      }

      SyncWindowPos();
      bool flingMoving = Math.Abs(_vx) > MoveAnimEps;
      UpdateBodyVisual(flingMoving, null); // 停在窗口顶上时回正面站姿
      _headFacing = _bodyFacing;
    }

    // 结束甩出：hurt=true 播受伤动画（调用方已把位置推出到墙边）；两种情况都排 800ms 后恢复自动游走
    private static void EndFling(bool hurt)
    {
      _vx = _vy = _tvx = _tvy = 0;
      _thrown = false;
      _thrownGrounded = false;
      _standingWindow = IntPtr.Zero;
      // 重力模式：从当前位置重新受重力探测支撑（已贴地时下一 tick 原地接住，空中则自然掉落，不瞬移）
      _gGrounded = false;
      StartIdleThenWalk(800);
      if (hurt) StartHurt();
      else UpdateBodyVisual(false, null);
    }

    // ---- 重力模式物理步进（控制模式与自动游走共用；平台查询复用甩出态的窗口枚举）----
    // desiredVx：期望水平速度（按键/AI；0=摩擦停住）；jumpRequest：本 tick 起跳请求（W 边沿）
    // groundedNow：步进后是否着地；hitWall：-1/1=本 tick 顶到左/右墙（AI 用于转向），0=无
    // 碰撞盒固定用常规主体盒：受伤/举姿只是视觉覆盖，物理边界不随姿势帧跳变
    private static void GravityStep(double desiredVx, bool jumpRequest, out bool groundedNow, out int hitWall)
    {
      hitWall = 0;
      Rect a = ActiveArea;
      ContentBounds cb = _cbNormal;
      double edgeL = cb.L * Scale * _zoom, edgeR = cb.R * Scale * _zoom;
      double edgeT = cb.T * Scale * _zoom, edgeB = (cb.B + FootSink) * Scale * _zoom;

      // 1) 着地支撑：站窗口则跟随其顶边（窗口关闭/走出边缘 → 掉落）；站地面则每 tick 强制贴地
      bool grounded = _gGrounded;
      if (grounded)
      {
        if (_standingWindow != IntPtr.Zero)
        {
          double pTop;
          if (TryGetStandPlatform(_standingWindow, _posX + edgeL, _posX + edgeR, a.Bottom, out pTop))
            _posY = pTop - edgeB;
          else { grounded = false; _standingWindow = IntPtr.Zero; }
        }
        else
        {
          _posY = a.Bottom - edgeB;
        }
      }

      // 2) 跳跃（仅着地瞬间）
      if (jumpRequest && grounded)
      {
        _vy = -JumpSpeed;
        grounded = false;
        _standingWindow = IntPtr.Zero;
      }

      // 3) 水平速度：着地用加速/摩擦积分；空中仅少量操控，无输入保留惯性
      if (grounded)
        _vx = Approach(_vx, desiredVx, desiredVx != 0 ? AccelK : FricK);
      else if (desiredVx != 0)
        _vx = Approach(_vx, desiredVx, AirControlK);

      // 4) 重力（_vy 跨 tick 持久化，着地时保持 0）
      if (!grounded) _vy = Math.Min(_vy + Gravity, MaxFallSpeed);

      // 5) X 轴位移 + 贴墙停住（不反弹；AI 据 hitWall 转向）
      _posX += _vx;
      if (_vx < 0 && _posX + edgeL <= a.Left)
      {
        _posX = a.Left - edgeL;
        hitWall = -1;
        _vx = 0;
      }
      else if (_vx > 0 && _posX + edgeR >= a.Right)
      {
        _posX = a.Right - edgeR;
        hitWall = 1;
        _vx = 0;
      }

      // 6) Y 轴位移：撞顶反弹；下落时地面优先，其次扫掠窗口平台（高速坠落播受伤，物理不中断）
      double prevFoot = _posY + edgeB;
      _posY += _vy;
      if (_vy < 0 && _posY + edgeT <= a.Top)
      {
        _posY = a.Top - edgeT;
        if (Math.Abs(_vy) >= WallHurtSpeed) StartHurt(); // 高速撞顶受伤，物理不中断
        _vy = -_vy * WallBounce;                          // 天花板反弹
      }
      else if (_vy > 0 && !grounded)
      {
        double newFoot = _posY + edgeB;
        if (newFoot >= a.Bottom)
        {
          _posY = a.Bottom - edgeB;
          if (_vy >= FloorHurtSpeed) StartHurt();
          if (_vy >= FloorBounceSpeed)
            _vy = -_vy * WallBounce; // 高速砸地弹起，保持空中
          else
          {
            _vy = 0;
            grounded = true;
            _standingWindow = IntPtr.Zero;
          }
        }
        else
        {
          double pTop;
          IntPtr hit = FindPlatformBelow(prevFoot, newFoot, _posX + edgeL, _posX + edgeR, out pTop);
          if (hit != IntPtr.Zero)
          {
            _posY = pTop - edgeB;
            if (_vy >= FloorHurtSpeed) StartHurt();
            if (_vy >= FloorBounceSpeed)
              _vy = -_vy * WallBounce; // 窗口平台也弹起（保持空中）
            else
            {
              _vy = 0;
              grounded = true;
              _standingWindow = hit;
            }
          }
        }
      }

      _gGrounded = grounded;
      groundedNow = grounded;
      SyncWindowPos();
    }

    #endregion

    private static void Tick(object sender, EventArgs e)
    {
      // 优先级：甩出飞行 > 重力模式（控制/游走）> 普通模式（控制/游走）
      if (_thrown) TickThrown();
      else if (_gravityMode)
      {
        if (_control) TickGravityControl();
        else TickAuto(); // 游走/睡眠/举光标调度共用 TickAuto，其移动段在重力模式下走 GravityStep
      }
      else if (_control) TickControl();
      else TickAuto();

      // 受伤动画推进（最高优先级姿势；播完 ApplyFrame 自动恢复常规两层）
      TickHurt();
      TickThumbup();

      // 头部动画：方向键按住时在 2 帧间循环（约 130ms/帧的眨眼节奏）
      if (_headAnimating)
      {
        _headAnimTick++;
        if (_headAnimTick >= 4)
        {
          _headAnimTick = 0;
          _headAnimFrame = 1 - _headAnimFrame;
          ApplyFrame();
        }
      }
    }

    // 惯性积分：实际速度向目标速度指数逼近（加速 AccelK / 摩擦 FricK），返回当前速率
    private static double IntegrateVelocity()
    {
      _vx = Approach(_vx, _tvx, _tvx != 0 ? AccelK : FricK);
      _vy = Approach(_vy, _tvy, _tvy != 0 ? AccelK : FricK);
      return Math.Sqrt(_vx * _vx + _vy * _vy);
    }

    private static double Approach(double v, double target, double k)
    {
      double nv = v + (target - v) * k;
      // 越过目标（反向加速中）或摩擦到足够小时，直接贴合目标
      if (target == 0)
      {
        if (Math.Abs(nv) < VelSnap) return 0;
      }
      else if ((target > 0 && nv >= target) || (target < 0 && nv <= target))
      {
        return target;
      }
      return nv;
    }

    // 朝向：水平分量优先（对应 A/D 优先规则），否则取竖直
    private static Dir FacingFromVelocity()
    {
      if (Math.Abs(_vx) >= Math.Abs(_vy) && Math.Abs(_vx) > 0.001)
        return _vx >= 0 ? Dir.Right : Dir.Left;
      if (Math.Abs(_vy) > 0.001)
        return _vy >= 0 ? Dir.Down : Dir.Up;
      return _bodyFacing;
    }

    // 按当前速度推进/定格行走动画；moving=false 时回到正面原图站姿
    private static void UpdateBodyVisual(bool moving, Dir? forceFacing)
    {
      Dir nd = forceFacing ?? (moving ? FacingFromVelocity() : Dir.Down);
      if (nd != _bodyFacing)
      {
        _bodyFacing = nd;
        _animFrame = 0;
        _animTick = 0;
      }
      if (moving)
      {
        _animTick++;
        if (_animTick >= 2)
        {
          _animTick = 0;
          _animFrame = (_animFrame + 1) % 10;
        }
      }
      else
      {
        _animFrame = 0;
        _animTick = 0;
      }
      ApplyFrame();
    }

    // 自动游走（带惯性：起步渐快、到点摩擦滑行后待机；全向随机角度）
    // 鼠标静止 45 秒 → 中断游走，走到光标下方摆出举姿；鼠标一动即取消
    private const int CursorIdleTriggerMs = 45000;

    private const double CursorHandsY = -12; // 举姿时双手在格子内的高度（DIP），光标停在手上方
    private const double CursorArriveDist = 2.0;

    private static void TickAuto()
    {
      _stateMs += 33;
      // 睡眠中：推进睡眠计时（右键菜单打开时暂停），睡够自然醒；不追踪鼠标、不移动
      if (_sleeping)
      {
        if (!_exitPosing)
        {
          _sleepMs += 33;
          if (_sleepMs >= _sleepDurationMs) WakeUp();
        }
        return;
      }
      if (_dragging || _exitPosing) return;

      // 入睡计时：正举着光标累计举姿时长，其余醒着活动累计清醒时长（拖拽/菜单期间不计）
      if (_holdingCursor)
      {
        _holdMs += 33;
        if (_holdMs >= _holdNeedMs) { StartSleep(); return; }
      }
      else
      {
        _awakeMs += 33;
        if (_awakeMs >= _awakeNeedMs) { StartSleep(); return; }
      }

      TrackCursor();

      // 重力模式不走向光标（目标点可能在空中），久置计时保持清零
      if (_gravityMode) _cursorIdleMs = 0;
      else if (_cursorAction) { TickCursorAction(); return; }

      if (_gravityMode)
      {
        // 重力游走：水平速度由 AI 的 _tvx 给出，垂直/平台/跳跃（AI 不跳）由 GravityStep 处理
        bool gNow;
        int hitWall;
        GravityStep(_tvx, false, out gNow, out hitWall);
        if (hitWall != 0 && _walking) { _tvx = -_tvx; _stateMs = 0; } // 顶墙转向，重置本段行走计时
        bool moving = Math.Abs(_vx) > MoveAnimEps;
        UpdateBodyVisual(moving, null);
        _headFacing = moving ? _bodyFacing : Dir.Down;
      }
      else
      {
        double spd = IntegrateVelocity();
        bool moving = spd > MoveAnimEps;

        if (moving)
        {
          _posX += _vx;
          _posY += _vy;
          BounceOffEdges();
          SyncWindowPos();
          UpdateBodyVisual(true, null);
          _headFacing = _bodyFacing;
        }
        else if (_tvx == 0 && _tvy == 0)
        {
          // 惯性耗尽 → 正面待机
          SyncWindowPos();
          UpdateBodyVisual(false, null);
          _headFacing = Dir.Down;
        }
      }

      if (_stateMs >= _stateDuration)
      {
        if (_walking) StartIdleThenWalk(Rnd.Next(1200, 2800));
        else StartWalking();
      }

      // 鼠标久置 → 去举光标（普通模式才触发）
      if (!_gravityMode && _cursorIdleMs >= CursorIdleTriggerMs) BeginCursorAction();
    }

    // 每 tick 轮询鼠标物理位置，统计静止时间；任何移动都会中断举光标行为
    private static void TrackCursor()
    {
      PointInterop cur;
      GetCursorPos(out cur);
      if (_lastCursorPX < 0)
      {
        _lastCursorPX = cur.X; _lastCursorPY = cur.Y;
        return;
      }
      if (Math.Abs(cur.X - _lastCursorPX) > 4 || Math.Abs(cur.Y - _lastCursorPY) > 4)
      {
        _lastCursorPX = cur.X; _lastCursorPY = cur.Y;
        _cursorIdleMs = 0;
        if (_cursorAction) CancelCursorAction(); // 鼠标动了，放下"光标"恢复游走
      }
      else
      {
        _cursorIdleMs += 33;
      }
    }

    private static void BeginCursorAction()
    {
      PointInterop cur;
      GetCursorPos(out cur);
      // 目标：水平居中于光标，纵向让上举的双手停在光标尖下方
      _targetX = cur.X / _dpi - CellW / 2;
      _targetY = cur.Y / _dpi - CursorHandsY;
      ClampTarget();

      _cursorAction = true;
      _approaching = true;
      _holdingCursor = false;
      _walking = true;
    }

    private static void TickCursorAction()
    {
      if (_approaching)
      {
        double dx = _targetX - _posX;
        double dy = _targetY - _posY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist <= CursorArriveDist)
        {
          _tvx = _tvy = 0; _vx = _vy = 0;
          _posX = _targetX; _posY = _targetY;
          _approaching = false;
          _holdingCursor = true;
          PlayEmbeddedSound("mouse" + Rnd.Next(1, 3) + ".mp3");
          _bodyFacing = Dir.Down;
          _headFacing = Dir.Down;
          _headAnimating = false;
          _animTick = _animFrame = 0;
          SyncWindowPos();
          ApplyFrame();
          return;
        }

        double k = Math.Min(1.0, 0.08 + dist * 0.04); // 远处快些、接近时减速
        _tvx = dx / dist * SpeedAuto * k;
        _tvy = dy / dist * SpeedAuto * k;
        double spd = IntegrateVelocity();
        _posX += _vx;
        _posY += _vy;
        ClampIntoScreen();
        SyncWindowPos();
        UpdateBodyVisual(spd > MoveAnimEps, null);
        _headFacing = _bodyFacing;
      }
      else if (_holdingCursor)
      {
        // 保持举姿、原地不动（鼠标移动会在 TrackCursor 中取消）
        _vx = _vy = _tvx = _tvy = 0;
      }
    }

    private static void ClampTarget()
    {
      Rect a = ActiveArea;
      if (_targetX < a.Left) _targetX = a.Left;
      if (_targetY < a.Top) _targetY = a.Top;
      if (_targetX + CellW > a.Right) _targetX = a.Right - CellW;
      if (_targetY + CellH > a.Bottom) _targetY = a.Bottom - CellH;
    }

    private static void CancelCursorAction()
    {
      if (!_cursorAction) return;
      _cursorAction = false;
      _approaching = false;
      _holdingCursor = false;
      _cursorIdleMs = 0;
      _tvx = _tvy = _vx = _vy = 0;
      StartIdleThenWalk(600);
    }

    // 入睡：可从普通游走或举光标姿势直接进入；中断光标行为但不走 Cancel 的恢复游走调度
    private static void StartSleep()
    {
      _cursorAction = false;
      _approaching = false;
      _holdingCursor = false;
      _cursorIdleMs = 0;
      _tvx = _tvy = _vx = _vy = 0;
      _sleeping = true;
      _sleepMs = 0;
      _sleepDurationMs = Rnd.Next(SleepDurationMinMs, SleepDurationMaxMs);
      SetWalking(false);
      _bodyFacing = Dir.Down;
      _headFacing = Dir.Down;
      _headAnimating = false;
      SyncWindowPos();
      ApplyFrame();
    }

    // 醒来（睡够 / 鼠标点击打断）：重置两路入睡计时并重新随机阈值
    private static void WakeUp()
    {
      if (!_sleeping) return;
      _sleeping = false;
      _sleepMs = 0;
      _awakeMs = 0;
      _awakeNeedMs = Rnd.Next(SleepAwakeMinMs, SleepAwakeMaxMs);
      _holdMs = 0;
      _holdNeedMs = Rnd.Next(SleepHoldMinMs, SleepHoldMaxMs);
      ApplyFrame();
      StartIdleThenWalk(800);
    }

    // 键盘操控：WASD 向量合成（可斜走，相反键抵消）；方向键头部独立
    private const double Diag = 0.70710678; // 1/√2

    private static void TickControl()
    {
      // 10 秒无任何 WASD/方向键输入 → 自动退回游走模式
      _controlIdleMs += 33;
      if (_controlIdleMs >= 10000) { ExitControl(); return; }

      // 按键状态由低级键盘钩子直接维护（操控期间这些键已被全局拦截）

      // 输入 → 目标速度（被拖动/右键菜单打开时清零；受伤闪烁期间仍可移动）
      double dx = 0, dy = 0;
      if (!_dragging && !_exitPosing)
      {
        dx = (_bodyKeys.Contains(Dir.Right) ? 1.0 : 0) - (_bodyKeys.Contains(Dir.Left) ? 1.0 : 0);
        dy = (_bodyKeys.Contains(Dir.Down) ? 1.0 : 0) - (_bodyKeys.Contains(Dir.Up) ? 1.0 : 0);
        if (dx != 0 && dy != 0) { dx *= Diag; dy *= Diag; }
      }
      bool hasInput = dx != 0 || dy != 0;
      _tvx = dx * SpeedControl;
      _tvy = dy * SpeedControl;

      double spd;
      if (_dragging || _exitPosing)
      {
        // 被抓住 / 右键菜单：实际速度瞬间清零，不产生位移
        _vx = _vy = 0;
        spd = 0;
      }
      else
      {
        spd = IntegrateVelocity();
        if (spd > VelSnap)
        {
          _posX += _vx;
          _posY += _vy;
          ClampIntoScreen();
          SyncWindowPos();
        }
      }

      bool moving = spd > MoveAnimEps;
      if (moving)
      {
        // 有输入时 A/D 优先；松开后滑行阶段按实际速度（水平分量优先）
        Dir nd = hasInput
            ? (dx != 0 ? (dx > 0 ? Dir.Right : Dir.Left)
                       : (dy > 0 ? Dir.Down : Dir.Up))
            : FacingFromVelocity();
        UpdateBodyVisual(true, nd);
      }
      else
      {
        // 停稳：正面站姿；头部若被方向键接管则保持独立
        UpdateBodyVisual(false, null);
        if (_headKeys.Count == 0) _headFacing = Dir.Down;
      }

      TickHeadControl();
    }

    // 头部方向键控制（普通/重力两种操控模式共用）：方向键栈顶决定头部朝向，无键则跟随身体
    private static void TickHeadControl()
    {
      if (_headKeys.Count > 0)
      {
        Dir hd = _headKeys[_headKeys.Count - 1];
        if (!_headAnimating || hd != _headFacing)
        {
          _headFacing = hd;
          _headAnimating = true;
          _headAnimFrame = 0;
          _headAnimTick = 0;
          ApplyFrame();
        }
      }
      else if (_headAnimating || _headFacing != _bodyFacing)
      {
        _headAnimating = false;
        _headAnimFrame = 0;
        _headFacing = _bodyFacing;
        ApplyFrame();
      }
    }

    // 重力模式键盘操控：A/D 左右走、W 着地时跳跃、S 无作用；方向键仍只控制头部
    private static void TickGravityControl()
    {
      // 10 秒无任何 WASD/方向键输入 → 自动退回重力游走
      _controlIdleMs += 33;
      if (_controlIdleMs >= 10000) { ExitControl(); return; }

      if (_dragging || _exitPosing)
      {
        // 被抓住/菜单：物理冻结（不调用 GravityStep），速度清零
        _vx = _vy = 0;
        UpdateBodyVisual(false, null);
        TickHeadControl();
        return;
      }

      double dx = (_bodyKeys.Contains(Dir.Right) ? 1.0 : 0) - (_bodyKeys.Contains(Dir.Left) ? 1.0 : 0);
      bool wDown = _bodyKeys.Contains(Dir.Up);
      bool jumpRequest = wDown && !_jumpLatch; // W 按下边沿触发；落地后需松开再按才能再跳
      _jumpLatch = wDown;

      bool grounded;
      int hitWall;
      GravityStep(dx * SpeedControl, jumpRequest, out grounded, out hitWall);

      bool moving = Math.Abs(_vx) > MoveAnimEps;
      if (moving)
      {
        Dir nd = dx != 0 ? (dx > 0 ? Dir.Right : Dir.Left) : FacingFromVelocity();
        UpdateBodyVisual(true, nd);
      }
      else
      {
        // 静止/空中：保持当前朝向的站姿；头部无方向键时正面
        UpdateBodyVisual(false, _bodyFacing);
        if (_headKeys.Count == 0) _headFacing = Dir.Down;
      }

      TickHeadControl();
    }

    private static void StartWalking()
    {
      if (_gravityMode)
      {
        // 重力游走：只在地面/平台上水平走，左/右二选一
        _tvx = 0.5 * (Rnd.Next(2) == 0 ? -SpeedAuto : SpeedAuto);
        _tvy = 0;
      }
      else
      {
        // 全向随机角度（0~2π），不限于八方向；速率固定为自动游走速度
        double angle = Rnd.NextDouble() * Math.PI * 2;
        _tvx = Math.Cos(angle) * SpeedAuto;
        _tvy = Math.Sin(angle) * SpeedAuto;
      }

      _walking = true;
      _stateMs = 0;
      _stateDuration = Rnd.Next(3000, 6000);
      SetWalking(true);
    }

    private static void StartIdleThenWalk(int idleMs)
    {
      _walking = false;
      _stateMs = 0;
      _stateDuration = idleMs;
      SetWalking(false);
      // 目标速度归零，实际速度靠摩擦力滑行衰减；完全停下后 TickAuto 切正面站姿
      _tvx = _tvy = 0;
    }

    // _walking 状态标志的唯一写入点（自动游走调度用；动画由实际速度驱动）
    private static void SetWalking(bool walking)
    {
      _walking = walking;
    }

    private static void ApplyFrame()
    {
      if (_bodyFrames == null) return;//两层合成布局
      // 睡眠：横帧独占（右键菜单打开时让位于退出姿势）
      if (_sleeping && !_exitPosing)
      {
        _sleepImage.Visibility = Visibility.Visible;
        _dragImage.Visibility = Visibility.Collapsed;
        _thumbImage.Visibility = Visibility.Collapsed;
        _bodyImage.Visibility = Visibility.Collapsed;
        _headImage.Visibility = Visibility.Collapsed;
        return;
      }
      _sleepImage.Visibility = Visibility.Collapsed;
      // 全身姿势（抓取 / 受伤闪烁 / 右键菜单）：32 宽帧由 _dragImage 承载
      // 优先级：抓取 > 受伤 > 右键菜单 > 点赞
      if (_dragPose || _hurtPlaying || _exitPosing)
      {
        BitmapSource pose;
        bool show = true;
        if (_dragPose) pose = _dragpose;
        else if (_hurtPlaying)
        {
          // 节拍计数对 2 取模，在"姿势帧/空帧"间交替 → 闪烁；空帧整个人物消失一拍
          pose = _hurtFrames[_hurtFrame % 2];
          show = pose != null;
        }
        else pose = _exitpose;
        _dragImage.Source = pose;
        _dragImage.RenderTransform = Transform.Identity;
        _dragImage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _thumbImage.Visibility = Visibility.Collapsed;
        _bodyImage.Visibility = Visibility.Collapsed;
        _headImage.Visibility = Visibility.Collapsed;
        return;
      }
      _dragImage.Visibility = Visibility.Collapsed;
      // 点赞：48 宽帧专用宽图层（窗口同步加宽，见 StartThumbup/SetThumbWindow）
      if (_thumbupPlaying)
      {
        _thumbImage.Source = _thumbupFrames[_thumbupFrame];
        _thumbImage.RenderTransform = Transform.Identity;
        _thumbImage.Visibility = Visibility.Visible;
        _bodyImage.Visibility = Visibility.Collapsed;
        _headImage.Visibility = Visibility.Collapsed;
        return;
      }
      _thumbImage.Visibility = Visibility.Collapsed;
      // 非抓取态：头/身两层必须都可见（抓取分支会折叠身体层，此处统一恢复，单一出口）
      _bodyImage.Visibility = Visibility.Visible;
      _headImage.Visibility = Visibility.Visible;
      // 举光标姿势
      if (_holdingCursor)
      {
        Canvas.SetTop(_bodyImage, 10 * Scale);
        _bodyImage.Source = _bodyHold;
        _headImage.Source = _headHold;
        _bodyImage.RenderTransform = Transform.Identity;
        _headImage.RenderTransform = Transform.Identity;
        // 举姿双臂在头部前方：身体层提到头部层之上（身盖头）
        Canvas.SetZIndex((UIElement)_bodyImage.Parent, 1);
        Canvas.SetZIndex((UIElement)_headImage.Parent, 0);
        return;
      }
      // 常规：恢复头盖身
      Canvas.SetZIndex((UIElement)_bodyImage.Parent, 0);
      Canvas.SetZIndex((UIElement)_headImage.Parent, 1);
      Canvas.SetTop(_bodyImage, 10 * Scale);
      _bodyImage.Source = _bodyFrames[(int)_bodyFacing][_animFrame];
      _headImage.Source = (_headAnimating && _headAnimFrame == 1)
          ? _headAnim[(int)_headFacing]
          : _headFrame[(int)_headFacing];

      double bodyFlip = _bodyFacing == Dir.Left ? -1 : 1;
      double headFlip = _headFacing == Dir.Left ? -1 : 1;
      _bodyImage.RenderTransform = new ScaleTransform(bodyFlip, 1, CellW / 2, 0);
      _headImage.RenderTransform = new ScaleTransform(headFlip, 1, CellW / 2, 0);
    }

    private static void BounceOffEdges()
    {
      Rect a = ActiveArea;
      // 碰撞边界取当前姿势的实际不透明像素包围盒（透明留白不计），随整体缩放放大
      ContentBounds cb = CurrentBounds;
      double edgeL = cb.L * Scale * _zoom, edgeR = cb.R * Scale * _zoom;
      double edgeT = cb.T * Scale * _zoom, edgeB = (cb.B + FootSink) * Scale * _zoom;
      if (_posX + edgeL <= a.Left)
      {
        _posX = a.Left - edgeL;
        _vx = Math.Abs(_vx); _tvx = Math.Abs(_tvx);
      }
      else if (_posX + edgeR >= a.Right)
      {
        _posX = a.Right - edgeR;
        _vx = -Math.Abs(_vx); _tvx = -Math.Abs(_tvx);
      }

      if (_posY + edgeT <= a.Top)
      {
        _posY = a.Top - edgeT;
        _vy = Math.Abs(_vy); _tvy = Math.Abs(_tvy);
      }
      else if (_posY + edgeB >= a.Bottom)
      {
        _posY = a.Bottom - edgeB;
        _vy = -Math.Abs(_vy); _tvy = -Math.Abs(_tvy);
      }
    }

    // 活动边界（所有碰撞/钳制的唯一来源）：系统工作区（底边即任务栏上沿，不进入任务栏区域）
    private static Rect ActiveArea
    {
      get { return SystemParameters.WorkArea; }
    }

    private static void ClampIntoScreen()
    {
      ClampIntoScreen(CurrentBounds);
    }

    // 指定碰撞盒的钳制：拖动时虽然显示抓取帧，但移动范围统一按常规主体盒，
    // 与键盘移动/自动游走/松手钳制保持一致（避免抓姿盒更高更宽导致范围不一致、松手跳位）
    private static void ClampIntoScreen(ContentBounds cb)
    {
      Rect a = ActiveArea;
      double edgeL = cb.L * Scale * _zoom, edgeR = cb.R * Scale * _zoom;
      double edgeT = cb.T * Scale * _zoom, edgeB = (cb.B + FootSink) * Scale * _zoom;
      if (_posX + edgeL < a.Left) _posX = a.Left - edgeL;
      if (_posY + edgeT < a.Top) _posY = a.Top - edgeT;
      if (_posX + edgeR > a.Right) _posX = a.Right - edgeR;
      if (_posY + edgeB > a.Bottom) _posY = a.Bottom - edgeB;
    }

    // 浮点内部位置 → 窗口坐标（仅此处吸附物理像素，避免亚像素虚影；内部精度不丢失）
    private static void SyncWindowPos()
    {
      _win.Left = Math.Round(_posX * _dpi) / _dpi;
      _win.Top = Math.Round(_posY * _dpi) / _dpi;
    }

    // 按游戏 001.000_player.anm2 的裁剪坐标预生成所有帧
    private static void BuildWalkFrames(BitmapSource sheet)
    {
      _bodyFrames = new CroppedBitmap[4][];
      _headFrame = new CroppedBitmap[4];
      _headAnim = new CroppedBitmap[4];
      for (int d = 0; d < 4; d++) _bodyFrames[d] = new CroppedBitmap[10];

      for (int i = 0; i < 10; i++)
      {
        Int32Rect vBody = i < 8
            ? new Int32Rect(i * 32, 32, 32, 32)
            : new Int32Rect(192 + (i - 8) * 32, 0, 32, 32);
        Int32Rect hBody = i < 8
            ? new Int32Rect(i * 32, 64, 32, 32)
            : new Int32Rect((i - 8) * 32, 96, 32, 32);

        _bodyFrames[(int)Dir.Down][i] = Crop(sheet, vBody);
        _bodyFrames[(int)Dir.Up][i] = Crop(sheet, vBody);
        _bodyFrames[(int)Dir.Right][i] = Crop(sheet, hBody);
        _bodyFrames[(int)Dir.Left][i] = Crop(sheet, hBody);
      }

      // 头：每方向第 1 帧（静态）+ 第 2 帧（眨眼，仅方向键操控时循环）
      _headFrame[(int)Dir.Down] = Crop(sheet, new Int32Rect(0, 0, 32, 32));
      _headFrame[(int)Dir.Up] = Crop(sheet, new Int32Rect(128, 0, 32, 32));
      _headFrame[(int)Dir.Right] = Crop(sheet, new Int32Rect(64, 0, 32, 32));
      _headFrame[(int)Dir.Left] = Crop(sheet, new Int32Rect(64, 0, 32, 32));

      _headAnim[(int)Dir.Down] = Crop(sheet, new Int32Rect(32, 0, 32, 32));
      _headAnim[(int)Dir.Up] = Crop(sheet, new Int32Rect(160, 0, 32, 32));
      _headAnim[(int)Dir.Right] = Crop(sheet, new Int32Rect(96, 0, 32, 32));
      _headAnim[(int)Dir.Left] = Crop(sheet, new Int32Rect(96, 0, 32, 32));

      // 举起动作：用于走到光标下举起光标的姿势
      _headHold = Crop(sheet, new Int32Rect(256, 0, 32, 32));
      _bodyHold = Crop(sheet, new Int32Rect(256, 32, 32, 32));

      // 特殊动作：全身帧，用于特殊交互姿势
      _dragpose = Crop(sheet, new Int32Rect(80, 272 + 4, 32, 64));
      // 受伤闪烁：染红姿势帧与空帧交替（显示/消失），节拍数由 HurtFrameCount 控制
      _hurtFrames = new BitmapSource[2];
      _hurtFrames[0] = TintRed(Crop(sheet, new Int32Rect(144 + 2, 208 + 4, 32, 64)));
      _hurtFrames[1] = null;
      _exitpose = Crop(sheet, new Int32Rect(16, 148, 32, 64));
      _sleeppose = Crop(sheet, new Int32Rect(207, 160, 64, 32));
      // 点赞动画：y=148 行 2 个 32x64 帧（x=80/144，间隔 64）
      _thumbupFrames = new CroppedBitmap[2];
      for (int i = 0; i < 2; i++)
        _thumbupFrames[i] = Crop(sheet, new Int32Rect(82 + i * 64, 150, 48, 64));

      // 扫描每类姿势的实际不透明像素包围盒（取同类全部帧并集，行走起伏/眨眼/朝向都不漏）
      // 常规：头帧按 y=0、身帧按 y=+10 放入 32x42 窗口格（含眨眼帧与举光标帧）
      BoundsAcc normal = new BoundsAcc();
      foreach (CroppedBitmap f in _headFrame) normal.Add(f, 0, 0);
      foreach (CroppedBitmap f in _headAnim) normal.Add(f, 0, 0);
      normal.Add(_headHold, 0, 0);
      normal.Add(_bodyHold, 0, 10);
      for (int d = 0; d < 4; d++)
        for (int i = 0; i < 10; i++)
          normal.Add(_bodyFrames[d][i], 0, 10);
      _cbNormal = normal.ToBox(32, 42);

      // 全身姿势帧（32x64）在窗口中按 y=-1 放置
      BoundsAcc pose = new BoundsAcc();
      pose.Add(_dragpose, 0, -1);
      pose.Add(_hurtFrames[0], 0, -1);
      pose.Add(_exitpose, 0, -1);
      _cbPose = pose.ToBox(32, 42);

      // 点赞帧（48x64）按 y=-1 放置；窗口点赞时右侧加宽 6 源格（总宽 38）
      BoundsAcc thumb = new BoundsAcc();
      foreach (CroppedBitmap f in _thumbupFrames) thumb.Add(f, 0, -1);
      _cbThumb = thumb.ToBox(32 + 6, 42);

      // 睡眠横帧（64x32）按 y=+10 放置
      BoundsAcc sleep = new BoundsAcc();
      sleep.Add(_sleeppose, 0, 10);
      _cbSleep = sleep.ToBox(32, 42);
    }

    // 拖动轨迹采样点（替代 C# 7 值元组，兼容 .NET Framework 自带 csc）：毫秒时间戳 + 鼠标物理坐标
    private sealed class FlingSample
    {
      public int T, X, Y;
    }

    // 不透明像素包围盒累积器：合并多张帧（帧放置位置由 offX/offY 指定，源像素单位）
    private sealed class BoundsAcc
    {
      private int _l = int.MaxValue, _t = int.MaxValue, _r = int.MinValue, _b = int.MinValue;

      public void Add(BitmapSource frame, int offX, int offY)
      {
        if (frame == null) return;
        Int32Rect? ab = AlphaBounds(frame);
        if (ab == null) return;
        Int32Rect v = ab.Value;
        if (v.X + offX < _l) _l = v.X + offX;
        if (v.Y + offY < _t) _t = v.Y + offY;
        if (v.X + v.Width + offX > _r) _r = v.X + v.Width + offX;
        if (v.Y + v.Height + offY > _b) _b = v.Y + v.Height + offY;
      }

      // 输出相对窗口格的内容盒；超出窗口的内容（如点赞/睡眠帧宽于窗口）夹到格内
      public ContentBounds ToBox(double gridW, double gridH)
      {
        return new ContentBounds(
          Math.Max(0, _l), Math.Max(0, _t),
          Math.Min(gridW, _r), Math.Min(gridH, _b));
      }
    }

    // 扫描帧内 alpha>0（实际有颜色）的像素包围盒；全透明帧返回 null
    private static Int32Rect? AlphaBounds(BitmapSource src)
    {
      FormatConvertedBitmap bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
      int w = bgra.PixelWidth, h = bgra.PixelHeight;
      byte[] buf = new byte[w * h * 4];
      bgra.CopyPixels(buf, w * 4, 0);
      int l = w, t = h, r = -1, b = -1;
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
          if (buf[(y * w + x) * 4 + 3] > 0)
          {
            if (x < l) l = x;
            if (x > r) r = x;
            if (y < t) t = y;
            if (y > b) b = y;
          }
      return r < 0 ? (Int32Rect?)null : new Int32Rect(l, t, r - l + 1, b - t + 1);
    }

    // 受伤帧染红：alpha 原样保留；R 取亮度×2 封顶 200、G/B 压到 35%（暗轮廓仍发黑，亮部呈红色，像以撒受伤闪红）
    private static BitmapSource TintRed(BitmapSource src)
    {
      FormatConvertedBitmap bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
      int w = bgra.PixelWidth, h = bgra.PixelHeight;
      byte[] buf = new byte[w * h * 4];
      bgra.CopyPixels(buf, w * 4, 0);
      for (int i = 0; i < buf.Length; i += 4) // 内存顺序 B,G,R,A
      {
        if (buf[i + 3] == 0) continue;       // 完全透明像素不动
        int origG = buf[i + 1]; // 原始 G（近似亮度），先存下，避免与下一行压暗后的 G 串值
        buf[i + 2] = (byte)Math.Min(255, origG * 110 / 100); // R
        buf[i + 1] = (byte)(origG * 60 / 100); // G
        buf[i] = (byte)(buf[i] * 60 / 100);         // B
      }
      BitmapSource result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
      result.Freeze();
      return result;
    }

    // 当前姿势对应的内容碰撞盒（分支优先级与 ApplyFrame 一致）
    private static ContentBounds CurrentBounds
    {
      get
      {
        if (_sleeping && !_exitPosing) return _cbSleep;
        if (_dragPose || _hurtPlaying || _exitPosing) return _cbPose;
        if (_thumbupPlaying) return _cbThumb;
        return _cbNormal;
      }
    }

    private static CroppedBitmap Crop(BitmapSource sheet, Int32Rect rect)
    {
      CroppedBitmap bmp = new CroppedBitmap(sheet, rect);
      bmp.Freeze();
      return bmp;
    }

    private static BitmapImage LoadEmbeddedImage(string resourceName)
    {
      Assembly asm = Assembly.GetEntryAssembly();
      using (Stream stream = asm.GetManifestResourceStream(resourceName))
      {
        BitmapImage bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
      }
    }
  }
}
