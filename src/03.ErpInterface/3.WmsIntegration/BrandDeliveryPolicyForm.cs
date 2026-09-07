using System;
using System.Windows.Forms;
using Infragistics.Win.UltraWinGrid;

namespace Portfolio.ErpInterface.WmsIntegration
{
    /// <summary>
    /// [담당업무 3] WMS 연동 화면 (WinForms + Infragistics) — 브랜드별 배송 정책.
    ///
    /// ERP 에서 받은 주문을 어떤 배송 방식으로 출고할지는 브랜드마다 다르다
    /// (기본 택배사, 당일배송 컷오프, 도서산간 처리, 합포장 허용 여부…).
    /// 이 화면에서 정한 정책이 수신 오퍼레이션의 데이터 복원 단계(ShipMethod 기본값 등)에 반영된다.
    /// </summary>
    public partial class BrandDeliveryPolicyForm : Form
    {
        private readonly UltraGrid _grid = new UltraGrid { Dock = DockStyle.Fill };
        private readonly IBrandDeliveryPolicyRepository _repo;

        public BrandDeliveryPolicyForm(IBrandDeliveryPolicyRepository repo)
        {
            _repo = repo;
            Text = "브랜드별 배송 정책";
            Controls.Add(_grid);
            Load += OnLoad;
            _grid.InitializeLayout += OnInitializeLayout;
            _grid.AfterCellUpdate += (s, e) => _dirty = true;
        }

        private bool _dirty;

        private void OnLoad(object sender, EventArgs e) => _grid.DataSource = _repo.LoadAll();

        private void OnInitializeLayout(object sender, InitializeLayoutEventArgs e)
        {
            var band = e.Layout.Bands[0];
            band.Columns["BrandCode"].CellActivation = Infragistics.Win.UltraWinGrid.Activation.NoEdit;
            band.Columns["DefaultCarrier"].ValueList = CarrierValueList();          // 콤보: 택배사 마스터
            band.Columns["SameDayCutoffHour"].MaskInput = "nn";
            band.Columns["AllowBundle"].Style = ColumnStyle.CheckBox;
            band.Columns["IslandSurcharge"].Format = "#,##0";
            e.Layout.Override.AllowAddNew = AllowAddNew.TemplateOnBottom;
            e.Layout.Override.RowSelectors = Infragistics.Win.DefaultableBoolean.True;
        }

        private ValueList CarrierValueList()
        {
            var vl = new ValueList();
            foreach (var c in _repo.Carriers()) vl.ValueListItems.Add(c.Code, c.Name);
            return vl;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty && MessageBox.Show("변경 사항을 저장할까요?", Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _grid.UpdateData();
                _repo.SaveAll((System.Collections.Generic.List<BrandDeliveryPolicy>)_grid.DataSource);
            }
            base.OnFormClosing(e);
        }
    }

    public sealed class BrandDeliveryPolicy
    {
        public string BrandCode { get; set; }
        public string DefaultCarrier { get; set; }
        public int SameDayCutoffHour { get; set; }
        public bool AllowBundle { get; set; }
        public decimal IslandSurcharge { get; set; }
    }

    public interface IBrandDeliveryPolicyRepository
    {
        System.Collections.Generic.List<BrandDeliveryPolicy> LoadAll();
        void SaveAll(System.Collections.Generic.List<BrandDeliveryPolicy> policies);
        System.Collections.Generic.IEnumerable<(string Code, string Name)> Carriers();
    }
}
