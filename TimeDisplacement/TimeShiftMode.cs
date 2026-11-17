using System.ComponentModel.DataAnnotations;

namespace TimeDisplacement
{
    public enum TimeShiftMode
    {
        [Display(Name = "黒→過去 / 白→現在", Description = "暗い部分ほど過去のフレーム、明るい部分ほど現在のフレームを表示します。")]
        PastFromBlack = 0,

        [Display(Name = "白→過去 / 黒→現在", Description = "明るい部分ほど過去のフレーム、暗い部分ほど現在のフレームを表示します。")]
        PastFromWhite = 1,

        [Display(Name = "前後 (50%灰基準・全体遅延)", Description = "全体を最大変位時間分だけ遅延させ、50%グレーを基準に黒で過去・白で未来方向へずらします。")]
        BidirectionalDelayed = 2,

        [Display(Name = "前後 (50%灰基準・未来クランプ)", Description = "現在フレームを50%グレー基準とし、黒で過去方向へずらします。(未来方向は現在フレームに固定されます)")]
        CenterClamped = 3,
    }
}
