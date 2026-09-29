using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

    private static double _tvx, _tvy;        // 目标速度（输入期望），_vx/_vy 为实际速度
    private static double _posX, _posY;      // 浮点内部位置（子像素累积，渲染时才吸附物理像素）

    private static Window _win;
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
    private static CroppedBitmap[] _hurtFrames;  // 受伤动作 4 帧（32x64）
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
    private const int SleepDurationMinMs = 240000, SleepDurationMaxMs = 360000; // 4~6min

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
      // 全身姿势帧图层（抓取/受伤共用）：32x64 源帧 ×1.5 = 48x96DIP，裁切原点与头部帧原点重合
      // Top 上移 1 源像素：64 高帧脚底（最大 y=42）对齐常规合成脚底（63DIP），不被窗口底边裁切
      _dragImage = new Image { Width = 32 * Scale, Height = 64 * Scale, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
      Canvas.SetTop(_dragImage, -1 * Scale);
      RenderOptions.SetBitmapScalingMode(_bodyImage, BitmapScalingMode.NearestNeighbor);
      RenderOptions.SetBitmapScalingMode(_headImage, BitmapScalingMode.NearestNeighbor);
      RenderOptions.SetBitmapScalingMode(_dragImage, BitmapScalingMode.NearestNeighbor);
      // 点赞专用宽图层：48x64 源帧 ×1.5 = 72x96DIP（手势探出 32 格外，不能复用 48 宽的 _dragImage，
      // 否则 Stretch.Fill 会横向压扁）；原点与头部帧重合，Top 同样上移 1 源像素
      _thumbImage = new Image { Width = 48 * Scale, Height = 64 * Scale, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
      Canvas.SetTop(_thumbImage, -1 * Scale);
      RenderOptions.SetBitmapScalingMode(_thumbImage, BitmapScalingMode.NearestNeighbor);

      // 身体层距顶部 15px（原始帧偏移 +10px × 1.5），头部层置顶（头盖身）
      _walker = new Grid { Width = CellW, Height = CellH };
      Canvas bodyLayer = new Canvas { Width = CellW, Height = CellH };
      Canvas.SetTop(_bodyImage, 10 * Scale);
      bodyLayer.Children.Add(_bodyImage);
      // 睡眠横帧：64x32 源帧 ×1.5 = 96x48DIP；原点与身体帧重合（同层同 Top），默认隐藏
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
      root.Children.Add(_walker);

      _win = new Window
      {
        Content = root,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        Topmost = true,
        ShowInTaskbar = false,
        ResizeMode = ResizeMode.NoResize,
        Width = CellW,
        Height = CellH
      };

      Rect area = SystemParameters.WorkArea;
      _posX = area.Left + (area.Width - CellW) / 2;
      _posY = area.Bottom - CellH;
      _win.Left = _posX;
      _win.Top = _posY;

      // 右键菜单：退出
      MenuItem exitItem = new MenuItem { Header = "do you truly want me to die?" };
      exitItem.Click += delegate { app.Shutdown(); };
      root.ContextMenu = new ContextMenu();
      root.ContextMenu.Items.Add(exitItem);
      // 菜单打开：中断举光标、冻结移动、切退出姿势；关闭：恢复（游走模式停顿片刻再走）
      root.ContextMenu.Opened += delegate
      {
        CancelCursorAction();
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

      root.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
      {
        wokeFromSleep = _sleeping;
        if (_sleeping) WakeUp(); // 点击（含拖拽/双击）立即打断睡眠，后续按正常点击流程进操控
        mouseDown = true;
        pastDrag = false;
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
          ClampIntoScreen();
          SyncWindowPos();
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
          if (!wokeFromSleep) StartThumbup();  // 唤醒睡眠的那一下只唤醒，不播点赞
        }
        else if (!_control) StartIdleThenWalk(800);
        ApplyFrame();                          // 恢复常规头/身两层（EnterControl 内部也会刷一次）
      };

      // 窗口句柄建立后取 DPI 缩放（150% 屏 = 1.5），用于坐标物理像素吸附
      _win.SourceInitialized += delegate
      {
        PresentationSource ps = PresentationSource.FromVisual(_win);
        if (ps != null) _dpi = ps.CompositionTarget.TransformToDevice.M11;
      };

      // 全局低级键盘钩子：操控期间拦截 WASD/方向键/Esc
      InstallKeyboardHook();
      AppDomain.CurrentDomain.ProcessExit += delegate { if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook); };

      // 焦点丢失（点了桌面/其他窗口）→ 退出操控，恢复自动游走，同时释放按键拦截
      _win.Deactivated += delegate { if (_control) ExitControl(); };

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

      ApplyFrame();
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
    private static void SetThumbWindow(bool wide)
    {
      _win.Width = wide ? CellW + ThumbWinExtra : CellW;
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

    private static void Tick(object sender, EventArgs e)
    {
      if (_control) TickControl();
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

      if (_cursorAction) { TickCursorAction(); return; }

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

      if (_stateMs >= _stateDuration)
      {
        if (_walking) StartIdleThenWalk(Rnd.Next(1200, 2800));
        else StartWalking();
      }

      // 鼠标久置 → 去举光标（普通调度让位）
      if (_cursorIdleMs >= CursorIdleTriggerMs) BeginCursorAction();
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
      Rect a = SystemParameters.WorkArea;
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

      // 头部：方向键栈顶决定朝向；无按键时跟随身体且不播放动画
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

    private static void StartWalking()
    {
      // 全向随机角度（0~2π），不再限于八方向；速率固定为自动游走速度
      double angle = Rnd.NextDouble() * Math.PI * 2;
      _tvx = Math.Cos(angle) * SpeedAuto;
      _tvy = Math.Sin(angle) * SpeedAuto;

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
        CroppedBitmap pose;
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
      Rect a = SystemParameters.WorkArea;
      if (_posX <= a.Left)
      {
        _posX = a.Left;
        _vx = Math.Abs(_vx); _tvx = Math.Abs(_tvx);
      }
      else if (_posX + CellW >= a.Right)
      {
        _posX = a.Right - CellW;
        _vx = -Math.Abs(_vx); _tvx = -Math.Abs(_tvx);
      }

      if (_posY <= a.Top)
      {
        _posY = a.Top;
        _vy = Math.Abs(_vy); _tvy = Math.Abs(_tvy);
      }
      else if (_posY + CellH >= a.Bottom)
      {
        _posY = a.Bottom - CellH;
        _vy = -Math.Abs(_vy); _tvy = -Math.Abs(_tvy);
      }
    }

    private static void ClampIntoScreen()
    {
      Rect a = SystemParameters.WorkArea;
      double w = CellW + (_thumbupPlaying ? ThumbWinExtra : 0); // 点赞播放时窗口临时加宽
      if (_posX < a.Left) _posX = a.Left;
      if (_posY < a.Top) _posY = a.Top;
      if (_posX + w > a.Right) _posX = a.Right - w;
      if (_posY + CellH > a.Bottom) _posY = a.Bottom - CellH;
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
      // 受伤闪烁：仅一个姿势帧，与空帧交替（显示/消失），节拍数由 HurtFrameCount 控制
      _hurtFrames = new CroppedBitmap[2];
      _hurtFrames[0] = Crop(sheet, new Int32Rect(144 + 2, 208 + 4, 32, 64));
      _hurtFrames[1] = null;
      _exitpose = Crop(sheet, new Int32Rect(16, 148, 32, 64));
      _sleeppose = Crop(sheet, new Int32Rect(206, 160, 64, 32));
      // 点赞动画：y=148 行 2 个 32x64 帧（x=80/144，间隔 64）
      _thumbupFrames = new CroppedBitmap[2];
      for (int i = 0; i < 2; i++)
        _thumbupFrames[i] = Crop(sheet, new Int32Rect(82 + i * 64, 150, 48, 64));
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
