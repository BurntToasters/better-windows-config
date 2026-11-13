using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Spike.Tree
{
    /// <summary>Row metrics shared by every row. Written once by LaunchOptions.Parse, before any row is bound.</summary>
    public static class TreeMetrics
    {
        public static double RowHeight { get; set; } = 20;
        public static double FontSize { get; set; } = 12;
        public static double IndentPx { get; set; } = 19;
    }

    /// <summary>
    /// One tree node. The flattened list in DenseTree holds references to the currently visible nodes.
    /// Children are created by Loader the first time the node is expanded and then cached.
    /// </summary>
    public sealed class TreeRow : INotifyPropertyChanged
    {
        private bool _expanded;
        private bool _selected;

        public TreeRow(string label, int depth, TreeRow? parent, bool hasChildren, Func<TreeRow, List<TreeRow>>? loader)
        {
            Label = label;
            Depth = depth;
            Parent = parent;
            HasChildren = hasChildren;
            Loader = loader;
        }

        public string Label { get; }
        public int Depth { get; }
        public TreeRow? Parent { get; }
        public bool HasChildren { get; }
        public Func<TreeRow, List<TreeRow>>? Loader { get; }
        public List<TreeRow>? Children { get; set; }

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                Raise(nameof(IsExpanded));
                Raise(nameof(PlusBarVisibility));
            }
        }

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Raise(nameof(IsSelected));
                Raise(nameof(SelectionBrush));
                Raise(nameof(LabelBrush));
            }
        }

        // Surface read by the row DataTemplate through x:Bind.
        public double RowHeight => TreeMetrics.RowHeight;
        public double FontSize => TreeMetrics.FontSize;
        public double IndentWidth => Depth * TreeMetrics.IndentPx;
        public Visibility ExpanderVisibility => HasChildren ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlusBarVisibility => IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        public Brush SelectionBrush => IsSelected ? RowBrushes.Highlight : RowBrushes.Clear;
        public Brush LabelBrush => IsSelected ? RowBrushes.OnHighlight : RowBrushes.Text;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Shared brushes so 200k rows do not allocate one brush each.</summary>
    internal static class RowBrushes
    {
        private static SolidColorBrush? _highlight;
        private static SolidColorBrush? _clear;
        private static SolidColorBrush? _onHighlight;
        private static Brush? _text;

        public static Brush Highlight => _highlight ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD7));
        public static Brush Clear => _clear ??= new SolidColorBrush(Colors.Transparent);
        public static Brush OnHighlight => _onHighlight ??= new SolidColorBrush(Colors.White);

        public static Brush Text
        {
            get
            {
                if (_text is null)
                {
                    object? theme = Application.Current?.Resources["TextFillColorPrimaryBrush"];
                    _text = theme as Brush ?? new SolidColorBrush(Colors.Black);
                }
                return _text;
            }
        }
    }
}
