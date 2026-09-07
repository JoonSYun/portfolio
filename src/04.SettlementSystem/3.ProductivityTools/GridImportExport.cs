using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ClosedXML.Excel;
using Infragistics.Win.UltraWinGrid;

namespace Portfolio.Settlement.ProductivityTools
{
    /// <summary>
    /// [담당업무 3] Import / Export 공통 컴포넌트 — 20여 개 입력 화면이 같은 두 메서드를 쓴다.
    ///   Export : 그리드의 보이는 컬럼·순서·헤더명 그대로 xlsx 로. 현업이 받은 파일이 곧 Import 템플릿.
    ///   Import : 헤더명으로 컬럼을 매칭(순서 무관). 미매칭 컬럼은 보고하고, 행 단위 오류는 모아서 한 번에 알려준다.
    /// </summary>
    public static class GridImportExport
    {
        public static void ExportToExcel(this UltraGrid grid, string title)
        {
            using (var dlg = new SaveFileDialog { Filter = "Excel|*.xlsx", FileName = $"{title}_{DateTime.Now:yyyyMMdd}.xlsx" })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var columns = VisibleColumns(grid);

                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add(title);
                    for (var c = 0; c < columns.Count; c++) ws.Cell(1, c + 1).Value = columns[c].Header.Caption;
                    ws.Row(1).Style.Font.Bold = true;

                    for (var r = 0; r < grid.Rows.Count; r++)
                        for (var c = 0; c < columns.Count; c++)
                            ws.Cell(r + 2, c + 1).Value = XLCellValue.FromObject(grid.Rows[r].Cells[columns[c]].Value);

                    ws.Columns().AdjustToContents();
                    wb.SaveAs(dlg.FileName);
                }
            }
        }

        public static ImportReport ImportFromExcel(this UltraGrid grid, Func<UltraGridRow, string> validateRow = null)
        {
            using (var dlg = new OpenFileDialog { Filter = "Excel|*.xlsx" })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return ImportReport.Cancelled;
                var report = new ImportReport();
                var columns = VisibleColumns(grid).ToDictionary(c => c.Header.Caption, c => c);

                using (var wb = new XLWorkbook(dlg.FileName))
                {
                    var ws = wb.Worksheet(1);
                    var headers = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
                    var mapping = headers.Select((h, i) => (Header: h, Index: i + 1, Column: columns.TryGetValue(h, out var col) ? col : null)).ToList();
                    report.UnmatchedHeaders.AddRange(mapping.Where(m => m.Column == null).Select(m => m.Header));

                    grid.BeginUpdate();
                    try
                    {
                        foreach (var xlRow in ws.RowsUsed().Skip(1))
                        {
                            var row = grid.DisplayLayout.Bands[0].AddNew();
                            foreach (var m in mapping.Where(m => m.Column != null))
                                row.Cells[m.Column].Value = xlRow.Cell(m.Index).IsEmpty() ? (object)DBNull.Value : xlRow.Cell(m.Index).Value.ToString();

                            var error = validateRow?.Invoke(row);
                            if (error != null) { report.RowErrors.Add((xlRow.RowNumber(), error)); row.Delete(false); }
                            else report.Imported++;
                        }
                    }
                    finally { grid.EndUpdate(); }
                }
                return report;
            }
        }

        private static List<UltraGridColumn> VisibleColumns(UltraGrid grid) =>
            grid.DisplayLayout.Bands[0].Columns.Cast<UltraGridColumn>().Where(c => !c.Hidden).OrderBy(c => c.Header.VisiblePosition).ToList();
    }

    public sealed class ImportReport
    {
        public static readonly ImportReport Cancelled = new ImportReport();
        public int Imported;
        public List<string> UnmatchedHeaders = new List<string>();
        public List<(int ExcelRow, string Error)> RowErrors = new List<(int, string)>();
    }
}
