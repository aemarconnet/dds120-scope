using System;
using System.Drawing;
using System.Windows.Forms;

namespace Dds120.UI
{
    // Small factory for consistently themed WinForms controls.
    internal static class UiKit
    {
        public static readonly Font Normal = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        static readonly Font TabFont = new Font("Segoe UI", 8.25f);

        public static Label Label(string text, bool dim = false)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = dim ? Theme.TextDim : Theme.Text,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 6, 0),
                Font = Normal,
            };
        }

        public static ComboBox Combo(params string[] items)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Control,
                ForeColor = Theme.Text,
                Font = Normal,
                DrawMode = DrawMode.OwnerDrawFixed,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2),
                IntegralHeight = false,
                MaxDropDownItems = 16,
            };
            c.Items.AddRange(items);
            c.DrawItem += DrawComboItem;
            return c;
        }

        static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            var c = (ComboBox)sender;
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(selected ? Theme.Accent : Theme.Control))
                e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, c.Items[e.Index].ToString(), c.Font, e.Bounds, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        public static NumericUpDown Number(decimal min, decimal max, int decimals, decimal increment)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = increment,
                BackColor = Theme.Control,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Normal,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2),
                TextAlign = HorizontalAlignment.Right,
            };
        }

        public static Button Button(string text, Color? back = null)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = back ?? Theme.Control,
                ForeColor = Theme.Text,
                Font = Bold,
                Dock = DockStyle.Fill,
                Height = 28,
                Margin = new Padding(0, 2, 0, 2),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Theme.ControlBorder;
            return b;
        }

        public static CheckBox Check(string text)
        {
            return new CheckBox
            {
                Text = text,
                AutoSize = true,
                ForeColor = Theme.Text,
                Font = Normal,
                Margin = new Padding(0, 5, 6, 3),
                FlatStyle = FlatStyle.Flat,
            };
        }

        // Adds a full-width row to the one-column side panel.
        static void AddToColumn(TableLayoutPanel column, Control c)
        {
            int r = column.RowCount++;
            column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Dock = DockStyle.Fill;
            column.Controls.Add(c, 0, r);
        }

        // A titled block with a label/control grid, stretched to the column width. With three columns
        // the grid is label | CH1 | CH2.
        public static TableLayoutPanel Section(TableLayoutPanel column, string title, Color accent, int columns = 2)
        {
            var header = new Panel { Height = 24, BackColor = Theme.PanelHeader, Margin = new Padding(0, 6, 0, 0) };
            var bar = new Panel { Width = 4, Dock = DockStyle.Left, BackColor = accent };
            var text = new Label
            {
                Text = title,
                ForeColor = Theme.Text,
                Font = Bold,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
            };
            header.Controls.Add(text);
            header.Controls.Add(bar);
            AddToColumn(column, header);
            return Grid(column, columns);
        }

        static TableLayoutPanel Grid(TableLayoutPanel column, int columns)
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = columns,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0),
                BackColor = Theme.Panel,
            };
            if (columns == 3)
            {
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            }
            else
            {
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            }
            AddToColumn(column, t);
            return t;
        }

        // A row of buttons that pick which page below them is shown; 'select' gets the index.
        public static Button[] Tabs(TableLayoutPanel column, string[] names, Action<int> select)
        {
            var strip = new TableLayoutPanel
            {
                ColumnCount = names.Length,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0),
                Padding = new Padding(0),
                BackColor = Theme.Window,
            };
            var buttons = new Button[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / names.Length));
                int index = i;
                Button b = Button(names[i]);
                b.Font = TabFont;
                b.Padding = new Padding(0);
                b.Margin = new Padding(i == 0 ? 0 : 1, 0, i == names.Length - 1 ? 0 : 1, 0);
                b.Click += (s, e) => select(index);
                strip.Controls.Add(b, i, 0);
                buttons[i] = b;
            }
            AddToColumn(column, strip);
            return buttons;
        }

        public static void SelectTab(Button[] tabs, Control[] pages, int index)
        {
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].BackColor = i == index ? Theme.Accent : Theme.Control;
                pages[i].Visible = i == index;
            }
        }

        // The body of one tab: a label/control grid like a section's, without a header.
        public static TableLayoutPanel Page(TableLayoutPanel column)
        {
            return Grid(column, 2);
        }

        // Returns the label so that the whole row can be hidden (see ShowRow).
        public static Label Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label l = Label(label, true);
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
            return l;
        }

        // label | CH1 control | CH2 control
        public static void Row3(TableLayoutPanel t, string label, Control a, Control b)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Label(label, true), 0, r);
            a.Margin = new Padding(0, 2, 3, 2);
            b.Margin = new Padding(3, 2, 0, 2);
            t.Controls.Add(a, 1, r);
            t.Controls.Add(b, 2, r);
        }

        // Rows that do not apply are hidden rather than greyed out; an AutoSize row collapses.
        public static void ShowRow(Label label, Control c, bool show)
        {
            label.Visible = show;
            c.Visible = show;
        }

        public static void Wide(TableLayoutPanel t, Control c)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, r);
            t.SetColumnSpan(c, 2);
            c.Dock = DockStyle.Fill;
        }

        // Two controls side by side spanning both columns.
        public static TableLayoutPanel Pair(Control a, Control b)
        {
            var p = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0), Dock = DockStyle.Fill };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            a.Margin = new Padding(0, 3, 3, 3);
            b.Margin = new Padding(3, 3, 0, 3);
            p.Controls.Add(a, 0, 0);
            p.Controls.Add(b, 1, 0);
            return p;
        }
    }
}
