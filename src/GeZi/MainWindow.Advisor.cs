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

// ---- 本文件是 MainWindow 的 partial 之一（代码规范化拆分） ----
// 职责：设置页左下角「风控参谋」卡片的渲染。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeZi.Core.Api;
using GeZi.Core.Support;
using GeZi.DownloadEngine.Storage;
using GeZi.Providers.Shared;

namespace GeZi
{
    public partial class MainWindow : Window
    {
        // ---------------- 风控参谋卡片渲染 ----------------
        // 只展示、不替用户改设置（用户 m17406 批准纯展示）。
        // 卡片内容三态：
        //   ① 冷启动（一条样本都没有）→ 磁盘介质兜底 + 一句「完成几次下载后…」。
        //   ② 收集中（某家样本 < AdvisorEngine.MinSamples）→ 显示「收集中 N/3」。
        //   ③ 已参谋（某家样本 ≥ MinSamples）→ 显示建议区间 + 红线。

        private static readonly PanKind[] AdvisorPanOrder =
        {
            PanKind.Quark, PanKind.Uc, PanKind.Aliyun, PanKind.Xunlei,
        };

        /// <summary>
        /// 点「查看」弹出小窗口展示各网盘建议（用户 m17843：改为弹窗，而非卡片内展开）。
        /// 数据在打开那一刻实时取（AdvisorStore 读是纯读、无锁），不缓存。
        /// 标题由 <see cref="DialogChrome.StyleWindow"/> 画在自绘标题栏里，内容区不再重复大标题。
        /// </summary>
        private void OnAdvisorViewClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Window
                {
                    Owner = this,
                    Title = "风控参谋",
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                DialogChrome.StyleWindow(dlg, 500, 430);

                var root = DialogChrome.DialogRoot();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 磁盘介质说明
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 建议列表（可滚动）
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 关闭按钮

                var disk = DialogChrome.Hint(DiskMediaText(_settings?.OutDir), 0, 12);
                Grid.SetRow(disk, 0);
                root.Children.Add(disk);

                var lines = new StackPanel();
                FillAdvisorLines(lines);
                var scroll = new ScrollViewer
                {
                    Content = lines,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                var card = DialogChrome.Card(scroll);
                Grid.SetRow(card, 1);
                root.Children.Add(card);

                var closeBtn = DialogChrome.PrimaryButton("关闭");
                closeBtn.MinWidth = 96;
                closeBtn.Click += (s2, e2) => dlg.Close();
                var btns = DialogChrome.ButtonRow(closeBtn);
                btns.Margin = new Thickness(0, 14, 0, 0);
                Grid.SetRow(btns, 2);
                root.Children.Add(btns);

                dlg.Content = root;
                dlg.ShowDialog();
            }
            catch { }
        }

        /// <summary>
        /// 把各网盘建议填进 <paramref name="target"/>：冷启动（无样本）给一行提示，
        /// 攒够样本按网盘列建议，一家都没攒够则底部补一句。弹窗内容由此生成。
        /// 全程只读 <see cref="AdvisorStore"/>（读是纯读、无锁），不写任何配置。
        /// </summary>
        private void FillAdvisorLines(Panel target)
        {
            var samples = AdvisorStore.GetAll();
            long now = DateTime.UtcNow.Ticks;

            // ① 冷启动：一条样本都没有 → 不给建议。
            if (samples.Count == 0)
            {
                target.Children.Add(BuildAdvisorHint(
                    "完成几次真实下载后，这里会根据记录给出各网盘的并发建议。", 0));
                return;
            }

            bool anyReady = false;
            foreach (var kind in AdvisorPanOrder)
            {
                var verdict = AdvisorEngine.AdviseFor(samples, (int)kind, now);
                if (verdict.SampleCount <= 0) continue;
                if (verdict.Ready) anyReady = true;
                target.Children.Add(BuildAdvisorRow(kind, verdict));
            }

            // 有样本但没一家攒够 3 次，底部补一句提示。
            if (!anyReady)
            {
                target.Children.Add(BuildAdvisorHint(
                    "每完成 3 次下载，就能给出一家网盘的并发建议。", 10));
            }
        }

        /// <summary>一行「网盘 + 结论」。结论文字由参谋结果拼，颜色走主题资源。</summary>
        private FrameworkElement BuildAdvisorRow(PanKind kind, AdvisorVerdict v)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = PanUrlParser.DisplayNameOf(kind),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)TryFindResource("TextPrimaryBrush"),
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            string conclusion = BuildAdvisorConclusion(v);
            var text = new TextBlock
            {
                Text = conclusion,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)TryFindResource("TextSecondaryBrush"),
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            return grid;
        }

        /// <summary>把参谋结果拼成一行人话结论。</summary>
        private static string BuildAdvisorConclusion(AdvisorVerdict v)
        {
            if (!v.Ready)
                return "收集中 " + v.SampleCount + "/" + AdvisorEngine.MinSamples;

            if (v.SuggestMax <= 0)
                return "样本不足，暂无可参考建议";

            string core = v.SuggestMin >= v.SuggestMax
                ? "建议 " + v.SuggestMax + " 路"
                : "建议 " + v.SuggestMin + "~" + v.SuggestMax + " 路";

            if (v.RedLine > 0)
                return core + " · 红线 " + v.RedLine + " 路";
            return core;
        }

        /// <summary>一行灰字说明（小字，不占大片高度）。</summary>
        private FrameworkElement BuildAdvisorHint(string text, double topMargin)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, topMargin, 0, 0),
                Foreground = (Brush)TryFindResource("TextTertiaryBrush"),
            };
        }

        /// <summary>磁盘介质的人话（机械盘 / 固态盘 / 未知）。</summary>
        private static string DiskMediaText(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "下载目录未设置，暂无法判断磁盘介质。";
            var kind = DiskMedia.DetectForPath(path);
            switch (kind)
            {
                case DiskMediaKind.Ssd: return "下载目录在固态盘，高并发随机写无寻道代价。";
                case DiskMediaKind.Hdd: return "下载目录在机械盘，高并发会被磁头寻道拖慢。";
                default: return "下载目录磁盘介质未知（网络路径或无法探测）。";
            }
        }
    }
}
