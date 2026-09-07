using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Infragistics.Win.UltraWinGrid;

namespace Portfolio.Settlement.ProductivityTools
{
    /// <summary>
    /// [담당업무 3] 현업 생산성 도구 — 엑셀 셀 범위를 그대로 붙여넣는 복사/붙여넣기 엔진.
    ///
    /// 현업은 정산 원천을 여전히 엑셀로 받는다. 20여 개 입력 화면마다 "엑셀에서 복사 → 그리드에 Ctrl+V" 가
    /// 되어야 시스템이 엑셀을 대체할 수 있었다. 클립보드의 탭/줄바꿈 구분 텍스트를 활성 셀 기준으로
    /// 그리드에 펼치고, 컬럼 타입에 맞춰 변환하며, 실패한 셀은 표시하되 나머지는 붙여넣는다.
    /// 모든 입력 화면이 이 엔진 하나를 붙여 쓴다 (UltraGrid 확장 메서드).
    /// </summary>
    public static class ExcelPasteEngine
    {
        public static PasteResult PasteFromClipboard(this UltraGrid grid)
        {
            if (!Clipboard.ContainsText()) return PasteResult.Empty;
            var text = Clipboard.GetText();
            var active = grid.ActiveCell;
            if (active == null) return PasteResult.Empty;

            var rows = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                           .Where(r => r.Length > 0)
                           .Select(r => r.Split('\t'))
                           .ToList();

            var band = grid.DisplayLayout.Bands[0];
            var visibleColumns = band.Columns.Cast<UltraGridColumn>()
                .Where(c => !c.Hidden).OrderBy(c => c.Header.VisiblePosition).ToList();
            var startCol = visibleColumns.IndexOf(active.Column);
            var startRowIndex = active.Row.Index;

            var result = new PasteResult();
            grid.BeginUpdate();
            try
            {
                for (var r = 0; r < rows.Count; r++)
                {
                    var gridRow = GetOrAddRow(grid, startRowIndex + r);
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        var colIndex = startCol + c;
                        if (colIndex >= visibleColumns.Count) break;
                        var column = visibleColumns[colIndex];
                        if (column.CellActivation == Activation.NoEdit) { result.Skipped++; continue; }

                        var cell = gridRow.Cells[column];
                        if (TryConvert(rows[r][c], column.DataType, out var value))
                        {
                            cell.Value = value;
                            result.Pasted++;
                        }
                        else
                        {
                            cell.Appearance.BackColor = System.Drawing.Color.MistyRose;  // 실패 셀 표시, 나머지는 계속
                            cell.ToolTipText = "변환 실패: " + rows[r][c];
                            result.Failed.Add((gridRow.Index, column.Key, rows[r][c]));
                        }
                    }
                }
            }
            finally { grid.EndUpdate(); }
            return result;
        }

        private static UltraGridRow GetOrAddRow(UltraGrid grid, int index)
        {
            while (grid.Rows.Count <= index) grid.DisplayLayout.Bands[0].AddNew();
            return grid.Rows[index];
        }

        /// <summary>엑셀 표기 관용: "1,234", "12.5%", "'0012", 빈칸 → 타입별 안전 변환.</summary>
        private static bool TryConvert(string raw, Type type, out object value)
        {
            var s = (raw ?? "").Trim().TrimStart('\'');
            value = null;
            if (s.Length == 0) { value = DBNull.Value; return true; }
            if (type == typeof(string)) { value = s; return true; }

            var numeric = s.Replace(",", "").Replace("원", "").TrimEnd('%');
            if (type == typeof(int)) { if (int.TryParse(numeric, out var i)) { value = i; return true; } return false; }
            if (type == typeof(decimal))
            {
                if (decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) { value = s.EndsWith("%") ? d / 100m : d; return true; }
                return false;
            }
            if (type == typeof(DateTime)) { if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("ko-KR"), DateTimeStyles.None, out var dt)) { value = dt; return true; } return false; }
            if (type == typeof(bool)) { value = s == "Y" || s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase); return true; }
            return false;
        }
    }

    public sealed class PasteResult
    {
        public static readonly PasteResult Empty = new PasteResult();
        public int Pasted; public int Skipped;
        public List<(int Row, string Column, string Raw)> Failed = new List<(int, string, string)>();
        public override string ToString() => $"붙여넣기 {Pasted}셀, 건너뜀 {Skipped}, 실패 {Failed.Count}";
    }
}
