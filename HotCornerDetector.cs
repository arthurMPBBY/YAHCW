using System;
using System.Drawing;
using System.Windows.Forms;

namespace YAHCW
{
    public enum CornerState
    {
        Disarmed,
        Armed,
        Dwelling,
        Triggered,
        Rearming,
        Cooldown
    }

    public enum CornerActionType
    {
        None,
        Trigger,
        IntervalChanged
    }

    public struct HotCornerAction
    {
        public CornerActionType ActionType;
        public int RecommendedIntervalMs;
        public Screen TriggerScreen;

        public static HotCornerAction None(int interval)
        {
            HotCornerAction a;
            a.ActionType = CornerActionType.None;
            a.RecommendedIntervalMs = interval;
            a.TriggerScreen = null;
            return a;
        }

        public static HotCornerAction IntervalChange(int interval)
        {
            HotCornerAction a;
            a.ActionType = CornerActionType.IntervalChanged;
            a.RecommendedIntervalMs = interval;
            a.TriggerScreen = null;
            return a;
        }

        public static HotCornerAction Trigger(Screen screen, int interval)
        {
            HotCornerAction a;
            a.ActionType = CornerActionType.Trigger;
            a.RecommendedIntervalMs = interval;
            a.TriggerScreen = screen;
            return a;
        }
    }

    public class HotCornerDetector
    {
        public CornerState State { get; private set; }
        public long DwellStartTime { get; private set; }
        public long CooldownStartTime { get; private set; }
        public int CurrentIntervalMs { get; private set; }

        private IntPtr _dwellHwnd = IntPtr.Zero;
        private uint _dwellPid = 0;
        private Point _dwellCorner;

        public IntPtr DwellHwnd { get { return _dwellHwnd; } }
        public uint DwellPid { get { return _dwellPid; } }

        public HotCornerDetector()
        {
            // Start disarmed: requires moving away outside release zone to arm
            State = CornerState.Disarmed;
            DwellStartTime = 0;
            CooldownStartTime = 0;
            CurrentIntervalMs = 100;
            _dwellHwnd = IntPtr.Zero;
            _dwellPid = 0;
        }

        public void ResetToArmed()
        {
            State = CornerState.Armed;
            DwellStartTime = 0;
            CooldownStartTime = 0;
            _dwellHwnd = IntPtr.Zero;
            _dwellPid = 0;
        }

        public void ResetToDisarmed()
        {
            State = CornerState.Disarmed;
            DwellStartTime = 0;
            CooldownStartTime = 0;
            _dwellHwnd = IntPtr.Zero;
            _dwellPid = 0;
        }

        public void NotifySuppressed()
        {
            // Suppression requires rearm: must leave release zone before arming again
            State = CornerState.Disarmed;
            DwellStartTime = 0;
            _dwellHwnd = IntPtr.Zero;
            _dwellPid = 0;
        }

        public void NotifyOverviewClosed(long nowMs)
        {
            State = CornerState.Rearming;
            CooldownStartTime = nowMs;
            DwellStartTime = 0;
            _dwellHwnd = IntPtr.Zero;
            _dwellPid = 0;
        }

        public static Point GetCornerPoint(Screen screen, string cornerName)
        {
            Rectangle b = screen.Bounds;
            string c = (cornerName ?? "TopLeft").ToLowerInvariant();

            if (c.Contains("right") && c.Contains("bottom"))
                return new Point(b.Right - 1, b.Bottom - 1);
            if (c.Contains("right"))
                return new Point(b.Right - 1, b.Top);
            if (c.Contains("bottom"))
                return new Point(b.Left, b.Bottom - 1);

            return new Point(b.Left, b.Top);
        }

        public static double CalculateDistance(Point p1, Point p2)
        {
            int dx = p1.X - p2.X;
            int dy = p1.Y - p2.Y;
            return Math.Sqrt((double)dx * dx + (double)dy * dy);
        }

        public static bool IsInTriggerZone(Point cursorPos, Point cornerPos, int triggerSizePx)
        {
            int dx = Math.Abs(cursorPos.X - cornerPos.X);
            int dy = Math.Abs(cursorPos.Y - cornerPos.Y);
            return dx <= triggerSizePx && dy <= triggerSizePx;
        }

        public static Point GetActivationCorner(Screen screen, Config config, Point cursor)
        {
            Point primary = GetCornerPoint(screen, config.Corner);
            Point secondary = GetCornerPoint(screen, "BottomRight");
            return CalculateDistance(cursor, secondary) < CalculateDistance(cursor, primary) ? secondary : primary;
        }

        public HotCornerAction ProcessTick(Point cursorPos, long nowMs, Screen screen, Config config, bool guardsPass)
        {
            return ProcessTick(cursorPos, nowMs, screen, config, guardsPass, IntPtr.Zero, 0);
        }

        public HotCornerAction ProcessTick(
            Point cursorPos,
            long nowMs,
            Screen screen,
            Config config,
            bool guardsPass,
            IntPtr currentFgHwnd,
            uint currentFgPid)
        {
            if (screen == null || config == null)
            {
                return HotCornerAction.None(CurrentIntervalMs);
            }

            Point corner = GetActivationCorner(screen, config, cursorPos);
            double distance = CalculateDistance(cursorPos, corner);
            bool inTrigger = IsInTriggerZone(cursorPos, corner, config.TriggerSizePx);
            bool inDwellZone = IsInTriggerZone(cursorPos, corner, Math.Max(config.TriggerSizePx, 8));

            // Adaptive interval calculation
            int targetInterval = (distance <= 100.0) ? 20 : 100;
            bool intervalChanged = (targetInterval != CurrentIntervalMs);
            CurrentIntervalMs = targetInterval;
            if (State == CornerState.Dwelling && _dwellCorner != corner)
            {
                DwellStartTime = nowMs;
                _dwellCorner = corner;
            }

            switch (State)
            {
                case CornerState.Disarmed:
                    // Requires leaving the release zone (distance >= RearmDistancePx) before arming
                    if (distance >= config.RearmDistancePx)
                    {
                        State = CornerState.Armed;
                    }
                    break;

                case CornerState.Armed:
                    if (inTrigger)
                    {
                        if (guardsPass)
                        {
                            State = CornerState.Dwelling;
                            DwellStartTime = nowMs;
                            _dwellCorner = corner;
                            _dwellHwnd = currentFgHwnd;
                            _dwellPid = currentFgPid;
                        }
                        else
                        {
                            // Suppression in corner requires rearm
                            State = CornerState.Disarmed;
                        }
                    }
                    break;

                case CornerState.Dwelling:
                    if (!inDwellZone)
                    {
                        // User flicked away before dwell completed
                        State = CornerState.Armed;
                        DwellStartTime = 0;
                        _dwellHwnd = IntPtr.Zero;
                        _dwellPid = 0;
                    }
                    else if (!guardsPass)
                    {
                        // Guard failed during dwell: suppression requires rearm
                        State = CornerState.Disarmed;
                        DwellStartTime = 0;
                        _dwellHwnd = IntPtr.Zero;
                        _dwellPid = 0;
                    }
                    else if (_dwellHwnd != IntPtr.Zero && (currentFgHwnd != _dwellHwnd || currentFgPid != _dwellPid))
                    {
                        // Foreground window identity changed during dwell: cancel dwell immediately
                        State = CornerState.Armed;
                        DwellStartTime = 0;
                        _dwellHwnd = IntPtr.Zero;
                        _dwellPid = 0;
                    }
                    else
                    {
                        long elapsed = nowMs - DwellStartTime;
                        if (elapsed >= config.DwellMs)
                        {
                            // Sustained dwell completed: Trigger!
                            State = CornerState.Triggered;
                            CooldownStartTime = nowMs;
                            DwellStartTime = 0;
                            _dwellHwnd = IntPtr.Zero;
                            _dwellPid = 0;
                            return HotCornerAction.Trigger(screen, CurrentIntervalMs);
                        }
                    }
                    break;

                case CornerState.Triggered:
                    // Wait until cursor moves away beyond rearm distance
                    if (distance >= config.RearmDistancePx)
                    {
                        State = CornerState.Cooldown;
                    }
                    break;

                case CornerState.Rearming:
                    if (distance >= config.RearmDistancePx)
                    {
                        State = CornerState.Cooldown;
                    }
                    break;

                case CornerState.Cooldown:
                    if ((nowMs - CooldownStartTime) >= config.CooldownMs)
                    {
                        if (!guardsPass)
                        {
                            State = CornerState.Disarmed;
                        }
                        else if (inTrigger)
                        {
                            State = CornerState.Dwelling;
                            DwellStartTime = nowMs;
                            _dwellCorner = corner;
                            _dwellHwnd = currentFgHwnd;
                            _dwellPid = currentFgPid;
                        }
                        else
                        {
                            State = CornerState.Armed;
                        }
                    }
                    break;
            }

            if (intervalChanged)
            {
                return HotCornerAction.IntervalChange(CurrentIntervalMs);
            }

            return HotCornerAction.None(CurrentIntervalMs);
        }
    }
}
