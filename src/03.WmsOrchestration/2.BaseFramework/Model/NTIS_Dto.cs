using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Saga;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;

namespace Portfolio.WmsOrchestration.Model
{
    #region NTIS Base Classes

    /// <summary>
    /// NTIS 파라미터 기본 클래스
    /// 모든 NTIS Param은 원본 스케줄러 대상 정보를 포함
    /// </summary>
    public class NTISParamBase
    {
        /// <summary>
        /// 원본 스케줄러 대상 정보
        /// Lock Key, API 정보, URI 등을 포함
        /// </summary>
        [NotMapped]
        public NTISTargetDto TargetInfo { get; set; }
    }

    #endregion

    #region NTIS Interfaces

    public interface INTISMasterData
    {
        public string BCODE { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
        public string IP { get; set; }
        public string MACADDR { get; set; }
    }

    public interface INTISStockCloseData
    {
        public string WMS_BCODE { get; set; }
        public string WAREHOUSE_CODE { get; set; }
        public string BASE_DATE { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
        public string IP { get; set; }
        public string MACADDR { get; set; }
    }

    public interface INTISDataBase
    {
        public string WMS_CENTERCODE { get; set; }
        public string WMS_BCODE { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
    }

    public interface INTISOrderData : INTISDataBase
    {
        public string IP { get; set; }
        public string MACADDR { get; set; }
    }

    #endregion

    #region NTIS Param Classes

    public sealed class NTISMasterParam : NTISParamBase, INTISMasterData
    {
        public string BCODE { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
        public string IP { get; set; }
        public string MACADDR { get; set; }
    }

    public sealed class NTISStockCloseData : NTISParamBase, INTISStockCloseData
    {
        public string WMS_BCODE { get; set; }
        public string WAREHOUSE_CODE { get; set; }
        public string BASE_DATE { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
        public string IP { get; set; }
        public string MACADDR { get; set; }
    }

    public sealed class NTISB2BOrderParam : NTISParamBase, INTISOrderData
    {
        private DataTable _wmsKeysetSlip;

        [JsonConverter(typeof(DataTableConverter))]
        public DataTable COM_KEYSET
        {
            get
            {
                if (_wmsKeysetSlip != null)
                    _wmsKeysetSlip.TableName = "UTY_COM_KEYSET";
                return _wmsKeysetSlip;
            }
            set
            {
                _wmsKeysetSlip = value;
            }
        }
        public string WMS_CENTERCODE { get; set; }
        public string WMS_BCODE { get; set; }
        public string SLIP_DIV { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
        public string IP { get; set; }
        public string MACADDR { get; set; }

        public static string GetSeparatorByApiCode(string code)
            => code switch
            {
                "TS-02-01" => "1",
                "TS-02-02" => "2",
                "TS-02-03" => "3",
                "TS-02-04" => "4",
                "TS-02-05" => "5",
                "TS-02-06" => "6",
                "TS-02-07" => "9",
                _ => "0"
            };
    }

    public sealed class B2COrderParam : NTISParamBase, INTISOrderData
    {
        public string WMS_CENTERCODE { get; set; }
        public string WMS_BCODE { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
        public string IP { get; set; }
        public string MACADDR { get; set; }
    }

    public abstract class NTISB2CResultDataBase : NTISParamBase, INTISDataBase
    {
        private DataTable _wmsKeysetIokey;
        [JsonConverter(typeof(DataTableConverter))]
        public DataTable WMS_KEYSET_IOKEY
        {
            get
            {
                if (_wmsKeysetIokey != null)
                    _wmsKeysetIokey.TableName = "UTY_WMS_KEYSET_IOKEY";

                return _wmsKeysetIokey;
            }
            set
            {
                _wmsKeysetIokey = value;
            }
        }
        public string WMS_CENTERCODE { get; set; }
        public string WMS_BCODE { get; set; }
        public string WMS_DATETIME { get; set; }
        public string WMS_USER { get; set; }
    }

    public sealed class NTISB2CRetrunParam : NTISB2CResultDataBase { }

    public sealed class NTISB2CResultParam : NTISB2CResultDataBase
    {
        public string PROC_DIV { get; set; }
    }

    public sealed class NTISECommerceParam : NTISParamBase
    {
        public string BCODE { get; set; }
        public string SHOP_CODE { get; set; }
    }

    #endregion

    #region TIS TARGET

    /// <summary>
    /// 스케줄러 실행 대상 정보 DTO
    /// Procedure: USP_SCHEDULER_TARGETMETHOD_GET
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISTargetDto
    {
        /// <summary>
        /// 브랜드 코드
        /// </summary>
        public string BCODE { get; set; }

        /// <summary>
        /// 호스트 시스템 (COM, SBN, SMT 등)
        /// </summary>
        public string HOST_SYSTEM { get; set; }

        /// <summary>
        /// 인터페이스 코드
        /// </summary>
        public string IF_CODE { get; set; }

        /// <summary>
        /// API 코드
        /// </summary>
        public string API_CODE { get; set; }

        /// <summary>
        /// API 명칭
        /// </summary>
        public string API_NAME { get; set; }

        /// <summary>
        /// URI (프로시저명 또는 메소드명)
        /// </summary>
        public string URI { get; set; }

        /// <summary>
        /// 파라미터 포함 URI (HTTP 호출용, 현재는 미사용)
        /// </summary>
        public string PARAM_URI { get; set; }

        /// <summary>
        /// 추가 파라미터 설정값
        /// "없음", "FEDEX|...", "DELIVUS|..." 등
        /// </summary>
        public string SET_PARAMS { get; set; }

        /// <summary>
        /// B2B 센터 코드
        /// </summary>
        public string B2B_CENTER_CODE { get; set; }

        /// <summary>
        /// B2C 센터 코드
        /// </summary>
        public string B2C_CENTER_CODE { get; set; }

        /// <summary>
        /// 기타 메시지 (실행 결과 등)
        /// </summary>
        public string ETC { get; set; }

        /// <summary>
        /// 로그 처리 대상 여부 (Y/N)
        /// IF_YN = '1'이면 'N', 아니면 'Y'
        /// </summary>
        public string 로그처리대상 { get; set; }

        /// <summary>
        /// Redis Lock Key 생성용
        /// </summary>
        public string GetLockKey()
        {
            return $"{BCODE}_{API_CODE}_{IF_CODE}";
        }

        /// <summary>
        /// 로그용 식별 정보
        /// </summary>
        public string GetLogIdentifier()
        {
            return $"{BCODE}|{API_CODE}|{API_NAME}";
        }

        /// <summary>
        /// 로그 처리가 필요한지 여부
        /// </summary>
        public bool ShouldLog()
        {
            return 로그처리대상 == "Y";
        }
    }

    #endregion

    #region NTIS Domain Events

    // ========== Master Sync Events ==========

    /// <summary>
    /// 창고마스터 동기화 이벤트
    /// - 창고마스터_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISWarehouseMasterSyncEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// 출고처마스터 동기화 이벤트
    /// - 출고처마스터_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISShipFromMasterSyncEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// 입고처마스터 동기화 이벤트
    /// - 입고처마스터_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISWhereToStockMasterSyncEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// 상품마스터 동기화 이벤트
    /// - 상품마스터_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISProductMasterSyncEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// 쇼핑몰마스터 동기화 이벤트
    /// - 쇼핑몰마스터_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISMallMasterSyncEvent : ChunkStepDomainEventBase { }

    // ========== B2B Events ==========

    /// <summary>
    /// B2B 지시 처리 이벤트
    /// - B2B지시_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISB2BOrderEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// B2B 발주지시 처리 이벤트
    /// - B2B발주지시_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISB2BPreOrderEvent : ChunkStepDomainEventBase { }

    // ========== B2C Order Events ==========

    /// <summary>
    /// B2C 출고지시 처리 이벤트
    /// - B2C출고지시_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISB2COutOrderEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// B2C 반품지시 처리 이벤트
    /// - B2C반품지시_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISB2CRetOrderEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// B2C 반품승인 처리 이벤트
    /// - B2C반품승인_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISB2CRetConfirmEvent : ChunkStepDomainEventBase { }

    // ========== B2C Result Events ==========

    /// <summary>
    /// B2C 출고 실적 처리 이벤트
    /// - B2C실적_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISB2COutResultEvent : ChunkStepDomainEventBase { }

    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class ImwebOrderResultEvent : ChunkStepDomainEventBase { }

    /// <summary>
    /// B2C 반품 실적 처리 이벤트
    /// - B2C반품실적_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISB2CRetStatusEvent : ChunkStepDomainEventBase { }

    // ========== Stock Close Event ==========

    /// <summary>
    /// 재고 마감 처리 이벤트
    /// - 가마감재고_수신_TEXT
    /// </summary>
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISStockCloseEvent : ChunkStepDomainEventBase { }

    // ========== E-Commerce Event ==========
    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class NTISECommerceEvent : ChunkStepDomainEventBase { }

    #endregion

    #region NTIS Domain Groups

    /// <summary>
    /// 도메인별 그룹핑 결과 컨테이너
    /// BCODE별로 묶인 스케줄러 대상을 URI별로 분류한 결과
    /// </summary>
    public class NTISDomainGroups
    {
        // Master Sync
        public List<NTISMasterParam> WarehouseMaster { get; set; } = new();
        public List<NTISMasterParam> ShipFromMaster { get; set; } = new();
        public List<NTISMasterParam> WhereToStockMaster { get; set; } = new();
        public List<NTISMasterParam> ProductMaster { get; set; } = new();
        public List<NTISMasterParam> MallMaster { get; set; } = new();

        // B2B
        public List<NTISB2BOrderParam> B2BOrder { get; set; } = new();
        public List<NTISB2BOrderParam> B2BPreOrder { get; set; } = new();

        // B2C Order
        public List<B2COrderParam> B2COutOrder { get; set; } = new();
        public List<B2COrderParam> B2CRetOrder { get; set; } = new();
        public List<B2COrderParam> B2CRetConfirm { get; set; } = new();

        // B2C Result
        public List<NTISB2CResultParam> B2COutResult { get; set; } = new();
        public List<NTISB2CRetrunParam> B2CRetStatus { get; set; } = new();

        // Stock Close
        public List<NTISStockCloseData> StockClose { get; set; } = new();

        // E-Commerce
        public List<NTISECommerceParam> ECommerce { get; set; } = new();
    }

    #endregion
}