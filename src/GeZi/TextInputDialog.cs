// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GeZi
{
    /// <summary>
    /// 单行文本输入对话框（重命名收藏 / 解析历史里的条目时用）。
    /// <para>
    /// 为什么自己写而不是用 <see cref="Microsoft.VisualBasic.Interaction.InputBox"/>：
    /// 那个要引 Microsoft.VisualBasic 程序集，而且外观是系统老式方框，
    /// 跟本项目「圆角卡片 + 设计令牌自绘」的整体风格完全搭不上（用户反馈过弹窗很丑）。
    /// 这里直接复用 <see cref="DialogChrome"/> 的卡片与按钮，风格统一。
    /// </para>
    /// </summary>
    internal static class TextInputDialog
    {
        /// <summary>
        /// 弹一个输入框。
        /// </summary>
        /// <param name="owner">父窗口（决定居中位置与模态归属）。</param>
        /// <param name="title">标题栏文字。</param>
        /// <param name="label">输入框上方的说明文字（可为空）。</param>
        /// <param name="initial">输入框里的初始内容。</param>
        /// <param name="okText">确认按钮文字。</param>
        /// <returns>用户确认后的内容；取消或点 X 返回 null。</returns>
        public static string Ask(Window owner, string title, string label,
                                string initial, string okText = "保存")
        {
            var dlg = new Window
            {
                Title = title,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };

            DialogChrome.StyleWindow(dlg, 420, 0);

            var root = DialogChrome.DialogRoot();

            var panel = new StackPanel();

            if (!string.IsNullOrEmpty(label))
            {
                var hint = DialogChrome.Hint(label, 0, 10);
                panel.Children.Add(hint);
            }

            var box = new TextBox
            {
                Text = initial ?? "",
                Height = 36,
                Padding = new Thickness(10, 6, 10, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                // 用项目统一的输入框样式（字体、边框、聚焦光标都对齐其它输入框）
                Style = Application.Current.TryFindResource("AppTextBox") as Style,
            };
            panel.Children.Add(box);

            root.Children.Add(panel);

            var ok = DialogChrome.PrimaryButton(okText);
            var cancel = DialogChrome.SecondaryButton("取消");

            string result = null;

            ok.Click += (s, e) =>
            {
                result = box.Text;
                dlg.DialogResult = true;
                dlg.Close();
            };
            cancel.Click += (s, e) =>
            {
                result = null;
                dlg.DialogResult = false;
                dlg.Close();
            };

            var btns = DialogChrome.ButtonRow(cancel, ok);
            btns.Margin = new Thickness(0, 18, 0, 0);
            root.Children.Add(btns);

            dlg.Content = root;

            // Enter 直接确认，Esc 取消 —— 跟系统对话框的习惯一致。
            dlg.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    result = box.Text;
                    dlg.DialogResult = true;
                    dlg.Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    result = null;
                    dlg.DialogResult = false;
                    dlg.Close();
                    e.Handled = true;
                }
            };

            dlg.Loaded += (s, e) =>
            {
                box.Focus();
                box.SelectAll();
            };

            dlg.ShowDialog();
            return result;
        }
    }
}
