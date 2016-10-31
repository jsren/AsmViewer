using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace RealtimeAsm
{
    /// <summary>
    /// Interaction logic for AsmGrid.xaml
    /// </summary>
    public partial class AsmGrid : UserControl
    {
        int nextRow = 0;

        Dictionary<AsmFunction, Tuple<int, int, TextBlock>> functionEntries
            = new Dictionary<AsmFunction, Tuple<int, int, TextBlock>>();

        public AsmGrid()
        {
            InitializeComponent();
        }

        public void Clear()
        {
            this.highlightRect.Visibility = Visibility.Collapsed;
            this.branchRect.Visibility = Visibility.Collapsed;
            this.branchArrow.Visibility = Visibility.Collapsed;

            this.asmGrid.Children.Clear();
            this.asmGrid.Children.Add(highlightRect);
            this.asmGrid.Children.Add(branchRect);
            this.asmGrid.Children.Add(branchArrow);

            this.functionEntries.Clear();
            nextRow = 0;
        }

        public void SelectFunction(string name)
        {
            highlightRect.Visibility = Visibility.Collapsed;
            if (name == null) return;
            name = name.Replace("void", "");
            try
            {
                foreach (var pair in functionEntries)
                {
                    if (pair.Key.Name.Trim().ToLower().EndsWith(name.Trim().ToLower()))
                    {
                        highlightRect.SetValue(Grid.RowProperty, pair.Value.Item1);
                        highlightRect.SetValue(Grid.RowSpanProperty, pair.Value.Item2 - pair.Value.Item1);
                        highlightRect.Visibility = Visibility.Visible;
                        break;
                    }
                }
            }
            catch { }
        }

        public void AddFunction(AsmFunction function)
        {
            var nameMargin = new Thickness(5, 0, 0, 0);

            var nameLabel = new TextBlock()
            {
                Margin = nameMargin,
                Text = function.Name.ToString(),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                FontWeight = FontWeights.Bold
            };

            var bgRect = new Rectangle()
            {
                Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            };
            bgRect.SetValue(Grid.RowProperty, nextRow);
            bgRect.SetValue(Grid.ColumnProperty, 0);
            bgRect.SetValue(Grid.ColumnSpanProperty, 4);
            this.asmGrid.Children.Add(bgRect);

            nameLabel.SetValue(Grid.ColumnProperty, 1);
            nameLabel.SetValue(Grid.ColumnSpanProperty, 3);

            this.asmGrid.RowDefinitions.Add(new RowDefinition());
            nameLabel.SetValue(Grid.RowProperty, nextRow);
            this.asmGrid.Children.Add(nameLabel);

            int startRow = nextRow++;
            foreach (var line in function.Assembly) {
                AddLine(line);
            }
            int endRow = nextRow;

            functionEntries.Add(function, new Tuple<int, int, TextBlock>(
                startRow, endRow, nameLabel));
        }


        public void AddLine(AsmLine line)
        {
            var offsetMargin   = new Thickness(5, 0, 0, 0);
            var opcodeMargin   = new Thickness(10, 0, 0, 0);
            var operandsMargin = new Thickness(10, 0, 0, 0);

            var offsetLabel = new TextBlock() {
                Margin = offsetMargin,
                Text = line.Offset.ToString("X"),
                HorizontalAlignment = HorizontalAlignment.Right, 
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6)),
            };
            var opcodeLabel = new TextBlock() {
                Margin = opcodeMargin,
                Text = line.Opcode.ToString(),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x82, 0xFF)),
                FontWeight = FontWeights.Bold
            };
            opcodeLabel.ToolTip = $"{line.SourceFile}:{line.SourceFileLine}";


            var operandsLabel = new TextBlock() {
                Margin = operandsMargin,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromRgb(0x72, 0x72, 0x72)),
                FontWeight = FontWeights.Medium
            };
            foreach (var run in GetRunsForOperands(line.Operands)) {
                operandsLabel.Inlines.Add(run);
            }

            var bgRect = new Rectangle() {
                Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            };
            bgRect.SetValue(Grid.RowProperty, nextRow);
            bgRect.SetValue(Grid.ColumnProperty, 0);
            bgRect.SetValue(Grid.ColumnSpanProperty, 4);

            bgRect.MouseEnter += AsmLine_MouseEnter;
            bgRect.MouseLeave += AsmLine_MouseLeave;

            offsetLabel.SetValue(Grid.ColumnProperty, 1);
            opcodeLabel.SetValue(Grid.ColumnProperty, 2);
            operandsLabel.SetValue(Grid.ColumnProperty, 3);

            this.asmGrid.RowDefinitions.Add(new RowDefinition());

            offsetLabel.SetValue(Grid.RowProperty, nextRow);
            opcodeLabel.SetValue(Grid.RowProperty, nextRow);
            operandsLabel.SetValue(Grid.RowProperty, nextRow);

            this.asmGrid.Children.Add(bgRect);
            this.asmGrid.Children.Add(offsetLabel);
            this.asmGrid.Children.Add(opcodeLabel);
            this.asmGrid.Children.Add(operandsLabel);

            nextRow++;
        }

        private void AsmLine_MouseLeave(object sender, MouseEventArgs e)
        {
            //branchRect.Visibility = Visibility.Collapsed;
        }

        TextBlock lastGreen;

        private void AsmLine_MouseEnter(object sender, MouseEventArgs e)
        {
            var obj = (sender as DependencyObject);
            if (obj == null) return;

            int row = (int)obj.GetValue(Grid.RowProperty);
            foreach (var pair in functionEntries)
            {
                if (row >= pair.Value.Item1 && row < pair.Value.Item2)
                {
                    var index = pair.Value.Item1;
                    foreach (var asm in pair.Key.Assembly)
                    {
                        if (++index < row) continue;

                        if (asm.Reference.HasValue && asm.Reference.Value.Name.Any())
                        {
                            if (lastGreen != null) {
                                lastGreen.Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x82, 0xFF));
                            }

                            ShowBranchIndicator(row, asm.Reference.Value);

                            foreach (UIElement child in asmGrid.Children)
                            {
                                if ((int)child.GetValue(Grid.RowProperty) == row
                                    && (int)child.GetValue(Grid.ColumnProperty) == 2)
                                {
                                    lastGreen = (TextBlock)child;
                                    ((TextBlock)child).Foreground = new SolidColorBrush(Colors.Green);
                                }
                            }

                        }
                        break;
                    }
                    break;
                }
            }

            highlightRect.SetValue(Grid.RowProperty, row);
            highlightRect.Visibility = Visibility.Visible;
        }

        private void AsmGrid_MouseMove(object sender, MouseEventArgs e)
        {
            var obj = (Mouse.DirectlyOver as DependencyObject);
            if (obj == null) return;

            try
            {
                int row = (int)obj.GetValue(Grid.RowProperty);
                foreach (var pair in functionEntries)
                {
                    if (row >= pair.Value.Item1 && row < pair.Value.Item2)
                    {
                        //highlightRect.SetValue(Grid.RowProperty, pair.Value.Item1);
                        //highlightRect.SetValue(Grid.RowSpanProperty, pair.Value.Item2 - pair.Value.Item1);
                        //highlightRect.Visibility = Visibility.Visible;
                        break;
                    }
                }
            }
            catch {  }
        }


        private int GetRowForReference(AsmReference reference)
        {
            foreach (var pair in functionEntries)
            {
                var function = pair.Key;

                if (function.Name == reference.Name)
                {
                    int targetOffset = reference.Offset;
                    if (function.Assembly.Count() != 0) {
                        targetOffset += function.Assembly.First().Offset;
                    }

                    int line = 0;
                    foreach (var asm in function.Assembly)
                    {
                        line++;
                        if (asm.Offset == targetOffset) {
                            return pair.Value.Item1 + line;
                        }
                    }
                    break;
                }
            }
            return -1;
        }


        private void ShowBranchIndicator(int row, AsmReference reference)
        {
            branchRect.Visibility = Visibility.Collapsed;
            branchArrow.Visibility = Visibility.Collapsed;

            int otherRow = GetRowForReference(reference);
            if (otherRow == row || otherRow == -1) return;

            branchRect.SetValue(Grid.RowProperty, Math.Min(row, otherRow));
            branchRect.SetValue(Grid.RowSpanProperty, Math.Abs(row - otherRow));
            branchArrow.SetValue(Grid.RowProperty, otherRow);

            branchRect.Visibility = Visibility.Visible;
            branchArrow.Visibility = Visibility.Visible;
        }


        private List<Run> GetRunsForOperands(string operands)
        {
            // Build runs
            Color literalColor = Color.FromRgb(0x72, 0x72, 0x72);
            Color registerColor = Color.FromRgb(0xE0, 0x42, 0x42);
            Color symbolColor = Colors.Black;

            var colors = new SolidColorBrush[] {
                new SolidColorBrush(symbolColor),
                new SolidColorBrush(literalColor),
                new SolidColorBrush(registerColor),  };

            const int symbolType = 0;
            const int literalType = 1;
            const int registerType = 2;

            var runs = new List<Run>();
            string currentRun = "";
            int runType = -1;

            var literalChars = "$-abcdefABCDEF";


            foreach (char c in operands)
            {
                // It's a register
                if (c == '%' || (runType == registerType && char.IsLetterOrDigit(c)))
                {
                    if (runType != registerType && runType != -1)
                    {
                        runs.Add(new Run(currentRun) { Foreground = colors[runType] });
                        currentRun = "";
                    }
                    runType = registerType;
                }
                // It's the start of a symbol
                else if (!char.IsLetterOrDigit(c) && !literalChars.Contains(c))
                {
                    if (runType != symbolType && runType != -1)
                    {
                        runs.Add(new Run(currentRun) { Foreground = colors[runType] });
                        currentRun = "";
                    }
                    runType = symbolType;
                }
                // It's the start of a literal
                else if (char.IsDigit(c) || literalChars.Contains(c))
                {
                    if (runType != literalType && runType != -1)
                    {
                        runs.Add(new Run(currentRun) { Foreground = colors[runType] });
                        currentRun = "";
                    }
                    runType = literalType;
                }

                currentRun += c;
                if (c == ',') currentRun += ' ';
            }
            // Final run
            if (runType != -1)
            {
                runs.Add(new Run(currentRun) { Foreground = colors[runType] });
            }

            return runs;
        }
    }
}
