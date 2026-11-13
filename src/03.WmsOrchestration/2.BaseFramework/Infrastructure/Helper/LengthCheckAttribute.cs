namespace Portfolio.WmsOrchestration.Infrastructure
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Field | AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    public class LengthCheckAttribute : Attribute
    {
        public int dataLength = 0;

        /* CDOE.20240117 HJC SBT에서 기준_매장에 업종 컬럼 길이 100으로 늘려달라하여 LENGTHCHECK 에서 30까지 잘라서 넣는걸로 처리하기 위해 에러대상체크 대상으로 사용하는 변수 추가*/
        public int useErrorCheck = 0;

        public LengthCheckAttribute(int length, int bFlag = 0)
        {
            /* bFlag */
            // 0:길이에러체크사용,   공백체크미사용  : true
            // 1:길이에러체크사용,   공백체크사용    : false
            // 2:길이에러체크미사용, 공백체크미사용  : ex) 상호, 대표자명
            // 3.길이에러체크미사용, 공백체크사용    : 대상 없음

            dataLength = length;
            useErrorCheck = bFlag;
        }
    }
}
