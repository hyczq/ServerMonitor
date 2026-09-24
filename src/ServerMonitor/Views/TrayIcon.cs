using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ServerMonitor.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ServerMonitor.Views
{
    /// <summary>
    /// 通知区域图标：窗口隐藏到后台之后的入口。
    ///
    /// 菜单刻意用 WPF 的 ContextMenu，不用 WinForms 的 ContextMenuStrip：
    /// Themes/Controls.xaml 里有隐式的 ContextMenu / MenuItem / Separator 样式
    /// （深色主题那一套，概览页的磁盘卡片右键菜单已经在用），WinForms 菜单会在
    /// 深色主题下冒出一块系统灰。
    ///
    /// 图标创建之后不再隐藏：窗口开着的时候也能用它「立即刷新」或「退出」，
    /// 这也符合"监控程序长期驻留"的心智模型。
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly Forms.NotifyIcon _notify;
        private readonly Drawing.Icon _icon;
        private readonly bool _ownsIcon;
        private readonly Window _owner;
        private readonly ContextMenu _menu;
        private bool _disposed;

        public TrayIcon(Window owner, Action onShow, Action onRefresh, Action onExit)
        {
            _owner = owner;
            _icon = LoadIcon(out _ownsIcon);

            _menu = new ContextMenu();
            _menu.Items.Add(BuildItem("显示主界面", onShow));
            _menu.Items.Add(BuildItem("立即刷新", onRefresh));
            _menu.Items.Add(new Separator());
            _menu.Items.Add(BuildItem("退出", onExit));

            _notify = new Forms.NotifyIcon();
            _notify.Icon = _icon;
            // NotifyIcon.Text 太长会抛异常，且通知区域的悬停提示本来就只能显示一行。
            // 气泡可能不显示，"程序还在跑"这句话主要靠这条 ToolTip 兜底。
            _notify.Text = "服务器监控台（双击打开窗口）";
            _notify.MouseUp += OnMouseUp;
            _notify.DoubleClick += delegate { if (onShow != null) onShow(); };
        }

        /// <summary>让图标出现在通知区域。</summary>
        public void Show()
        {
            _notify.Visible = true;
        }

        /// <summary>
        /// 第一次隐藏到通知区域时提示一次。
        ///
        /// 气泡是装饰性的：Win10/11 上，没有注册 AUMID 的便携 exe 大概率不显示它，
        /// 而且它只出现几秒。所以"程序还在跑"这个关键信息必须写进 ToolTip
        /// （见构造函数），不能只放在气泡里。
        /// </summary>
        public void ShowFirstHideBalloon()
        {
            try
            {
                _notify.BalloonTipTitle = "服务器监控台仍在运行";
                _notify.BalloonTipText = "窗口已隐藏到通知区域，采集与告警推送继续进行。双击图标可以重新打开窗口。";
                _notify.BalloonTipIcon = Forms.ToolTipIcon.Info;
                _notify.ShowBalloonTip(5000);
            }
            catch (Exception ex)
            {
                Logger.Warn("托盘", "提示气泡显示失败（不影响后台运行）：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // 菜单
        // ---------------------------------------------------------------

        private static MenuItem BuildItem(string header, Action action)
        {
            var item = new MenuItem();
            item.Header = header;
            item.Click += delegate { if (action != null) action(); };
            return item;
        }

        private void OnMouseUp(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button != Forms.MouseButtons.Right) return;
            ShowMenu();
        }

        private void ShowMenu()
        {
            // 抢前台：不抢的话，在别的程序上点一下菜单不会收起，用户得点两次。
            // 主窗口隐藏时它的 HWND 依然有效（Hide 不销毁窗口），但隐藏窗口通常
            // 拿不到前台——所以这里记一条 Debug，真出现"要点两次"时按日志定位，
            // 改成随手创建一个 1x1 的可见窗口来抢前台。
            try
            {
                IntPtr handle = _owner != null ? new WindowInteropHelper(_owner).Handle : IntPtr.Zero;
                if (handle != IntPtr.Zero && !SetForegroundWindow(handle))
                {
                    Logger.Debug("托盘", "SetForegroundWindow 未生效，托盘菜单可能需要在别处点第二次才收起。");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("托盘", "抢前台失败：" + ex.Message);
            }

            _menu.Placement = PlacementMode.MousePoint;
            _menu.PlacementTarget = _owner;
            _menu.IsOpen = true;
        }

        // ---------------------------------------------------------------
        // 图标
        // ---------------------------------------------------------------

        /// <summary>
        /// 取通知区域用的图标，逐级降级：内嵌 app.ico → exe 自身的图标 → 系统图标。
        /// 三级都不抛异常：没有图标也应该能后台运行，顶多是样子不对。
        /// </summary>
        private static Drawing.Icon LoadIcon(out bool ownsIcon)
        {
            ownsIcon = true;

            // 必须给尺寸：不给的话会拿最大的一帧（256×256 的 PNG 条目）再缩下来，
            // 在通知区域里边缘发糊。给尺寸后由 GDI 直接挑最合适的那一条。
            try
            {
                var uri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
                System.Windows.Resources.StreamResourceInfo resource = Application.GetResourceStream(uri);
                if (resource != null && resource.Stream != null)
                {
                    using (System.IO.Stream stream = resource.Stream)
                    {
                        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
                    }
                }
                Logger.Warn("托盘", "没找到内嵌图标 app.ico，改用程序自身的图标。");
            }
            catch (Exception ex)
            {
                Logger.Warn("托盘", "内嵌图标加载失败，改用程序自身的图标：" + ex.Message);
            }

            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                string path = entry != null ? entry.Location : null;
                if (!string.IsNullOrEmpty(path))
                {
                    Drawing.Icon extracted = Drawing.Icon.ExtractAssociatedIcon(path);
                    if (extracted != null) return extracted;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("托盘", "读取程序图标失败，改用系统自带图标：" + ex.Message);
            }

            // 最后兜底。SystemIcons 返回的是共享实例，绝不能 Dispose（见 Dispose）。
            ownsIcon = false;
            return Drawing.SystemIcons.Application;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_menu.IsOpen) _menu.IsOpen = false;

                // 顺序要紧：先 Visible = false 再 Dispose，否则通知区域会留下一个
                // 要点一下才消失的僵尸图标。
                _notify.Visible = false;
                _notify.Icon = null;
                _notify.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn("托盘", "释放通知区域图标时出错：" + ex.Message);
            }

            // SystemIcons.* 是共享实例，Dispose 它会连累同进程里其它用到它的地方
            if (_ownsIcon && _icon != null) _icon.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
