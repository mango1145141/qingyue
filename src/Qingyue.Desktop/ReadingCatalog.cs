namespace EpubKindleFix;

public static class ReadingCatalog
{
    private static DiscoveryBook B(string id, string title, string author, string genres, string description,
        string aliases = "", string source = "https://www.gutenberg.org/ebooks/") => new(id, title, author,
            genres.Split('|'), description, aliases.Split('|', StringSplitOptions.RemoveEmptyEntries), source);

    // Editorial topic labels for discovery, not an exhaustive or live library catalogue.
    private static readonly DiscoveryBook[] Prose =
    [
        B("ln-literary", "文学少女", "野村美月", "轻小说|文学小说", "在校园与文学作品之间，读青春的秘密与成长。", "文學少女|野村美月", "https://isbn.ncl.edu.tw/"),
        B("ln-konosuba", "为美好的世界献上祝福！", "晓枣", "轻小说|幻想冒险", "带一点喜剧感的异世界冒险。", "為美好的世界獻上祝福|美好世界|この素晴らしい世界に祝福|暁なつめ|晓夏目", "https://promo.kadokawa.co.jp/sneakerbunko/konosuba/"),
        B("ln-spice", "狼与香辛料", "支仓冻砂", "轻小说|幻想冒险", "旅途、交易与人与人之间的信任。", "狼與辛香料|狼与辛香料|支倉凍砂|spiceandwolf", "https://www.kadokawa.com.tw/products/spice-and-wolf"),
        B("ln-kino", "奇诺之旅", "时雨泽惠一", "轻小说|哲学思考", "在一个个陌生国度里，重新思考日常的规则。", "奇諾之旅|時雨澤惠一|kino", "https://www.kadokawa.com.tw/"),
        B("ln-haruhi", "凉宫春日的忧郁", "谷川流", "轻小说|科幻", "校园日常里突然闯入的不寻常世界。", "涼宮ハルヒ|涼宮春日|凉宫春日", "https://store.kadokawa.co.jp/shop/series/series00074005"),
        B("ln-rezero", "Re：从零开始的异世界生活", "长月达平", "轻小说|幻想冒险", "在反复重来的冒险中寻找选择的代价。", "从零开始的异世界|從零開始的異世界|rezero|長月達平", "https://www.kadokawa.co.jp/product/301312000335/"),
        B("sf-threebody", "三体", "刘慈欣", "科幻", "从地球望向宇宙中的文明与选择。", "三體|threebody|刘慈欣|劉慈欣", "https://www.sfw.com.cn/"),
        B("sf-lightning", "球状闪电", "刘慈欣", "科幻", "围绕一个自然谜题展开的科学想象。", "球狀閃電|balllightning", "https://www.sfw.com.cn/go-a1563.htm"),
        B("sf-supernova", "超新星纪元", "刘慈欣", "科幻", "当世界的规则突然改变，未来如何继续。", "超新星紀元", "https://www.sfw.com.cn/"),
        B("sf-timemachine", "时间机器", "H. G. 威尔斯", "科幻", "跟随时间旅行，看见遥远未来的社会。", "時間機器|timemachine|hgwells|威尔斯", "https://www.gutenberg.org/ebooks/35"),
        B("sf-sea", "海底两万里", "儒勒·凡尔纳", "科幻|幻想冒险", "乘上鹦鹉螺号，探索海洋深处。", "海底兩萬里|twentythousandleagues|凡尔纳|凡爾納|julesverne", "https://www.gutenberg.org/ebooks/164"),
        B("mystery-holmes", "福尔摩斯探案集", "阿瑟·柯南·道尔", "悬疑推理", "观察细节，跟随推理破解一桩桩谜案。", "福爾摩斯|福尔摩斯|sherlockholmes|conandoyle|柯南道尔", "https://www.gutenberg.org/ebooks/1661"),
        B("mystery-moonstone", "月亮宝石", "威尔基·柯林斯", "悬疑推理", "从不同叙述者的视角拼起失窃之谜。", "月亮寶石|moonstone|wilkiecollins", "https://www.gutenberg.org/ebooks/155"),
        B("mystery-roger", "罗杰疑案", "阿加莎·克里斯蒂", "悬疑推理", "在熟悉的小镇里追索意想不到的真相。", "羅傑疑案|罗杰艾克罗伊德谋杀案|agathachristie|阿加莎", "https://www.agathachristie.com/stories/the-murder-of-roger-ackroyd"),
        B("mystery-orient", "东方快车谋杀案", "阿加莎·克里斯蒂", "悬疑推理", "一列被大雪困住的列车，一场封闭空间推理。", "東方快車謀殺案|murderontheorientexpress", "https://www.agathachristie.com/stories/murder-on-the-orient-express"),
        B("mystery-none", "无人生还", "阿加莎·克里斯蒂", "悬疑推理", "孤岛上的十个人，被线索逐渐收紧包围。", "無人生還|andthentherewerenone", "https://www.agathachristie.com/stories/and-then-there-were-none"),
        B("fantasy-hobbit", "霍比特人", "J. R. R. 托尔金", "幻想冒险", "从家门前开始，走进一场意外的远行。", "哈比人|thehobbit|托爾金|tolkien", "https://www.tolkienestate.com/"),
        B("fantasy-rings", "魔戒", "J. R. R. 托尔金", "幻想冒险", "关于友谊、责任与漫长旅途的幻想史诗。", "指环王|指環王|lordoftherings", "https://www.tolkienestate.com/"),
        B("fantasy-alice", "爱丽丝梦游仙境", "刘易斯·卡罗尔", "幻想冒险", "在奇妙规则的世界里，保留好奇心。", "愛麗絲夢遊仙境|aliceinwonderland|lewiscarroll", "https://www.gutenberg.org/ebooks/11"),
        B("fantasy-earthsea", "地海巫师", "厄休拉·勒古恩", "幻想冒险", "魔法少年的旅程，也是面对自我的旅程。", "地海巫師|wizardofearthsea|勒瑰恩|ursulaleguin", "https://www.ursulakleguin.com/a-wizard-of-earthsea"),
        B("lit-call", "呐喊", "鲁迅", "文学小说", "短篇故事里的生活、处境与人的声音。", "吶喊|魯迅|l uxun", "https://www.gutenberg.org/ebooks/25328"),
        B("lit-rickshaw", "骆驼祥子", "老舍", "文学小说", "跟随一个普通人的愿望与命运走过旧北京。", "駱駝祥子", "https://www.pep.com.cn/"),
        B("lit-pride", "傲慢与偏见", "简·奥斯汀", "文学小说", "从日常相处中看见误解、判断与爱情。", "傲慢與偏見|prideandprejudice|janeausten|奥斯汀", "https://www.gutenberg.org/ebooks/1342"),
        B("lit-jane", "简·爱", "夏洛蒂·勃朗特", "文学小说", "一位女性追寻独立、尊严与自己的生活。", "简爱|簡愛|janeeyre|charlottebronte", "https://www.gutenberg.org/ebooks/1260"),
        B("lit-cities", "双城记", "查尔斯·狄更斯", "文学小说|历史", "在时代剧变中读个人的选择与牺牲。", "雙城記|taleoftwocities|charlesdickens|狄更斯", "https://www.gutenberg.org/ebooks/98"),
        B("lit-metamorphosis", "变形记", "弗兰茨·卡夫卡", "文学小说|哲学思考", "从一次离奇变化，看见日常关系的裂缝。", "變形記|metamorphosis|franzkafka|卡夫卡", "https://www.gutenberg.org/ebooks/5200"),
        B("history-shiji", "史记", "司马迁", "历史", "从人物传记进入中国古代的历史。", "史記|司馬遷", "https://ctext.org/shiji/zh"),
        B("history-three", "三国志", "陈寿", "历史", "从纪传史料重新认识三国人物。", "三國志|陳壽", "https://ctext.org/sanguozhi/zh"),
        B("history-herodotus", "历史", "希罗多德", "历史", "从古代的旅行、战争与传闻认识文明。", "希羅多德|herodotus|histories", "https://www.gutenberg.org/ebooks/2707"),
        B("history-thucydides", "伯罗奔尼撒战争史", "修昔底德", "历史", "观察战争、政治与城邦之间的选择。", "伯羅奔尼撒|thucydides", "https://www.gutenberg.org/ebooks/7142"),
        B("philo-analects", "论语", "孔子及其弟子", "哲学思考", "从简短对话里读学习、相处与自省。", "論語|孔子|confucius", "https://ctext.org/analects/zh"),
        B("philo-dao", "道德经", "老子", "哲学思考", "从简练的古典语言里思考自然与行动。", "道德經|道德经|laozi", "https://ctext.org/dao-de-jing/zh"),
        B("philo-meditations", "沉思录", "马可·奥勒留", "哲学思考|心理与成长", "日常生活中关于自省与处事的笔记。", "沉思錄|meditations|marcusaurelius|奥勒留", "https://www.gutenberg.org/ebooks/2680"),
        B("philo-republic", "理想国", "柏拉图", "哲学思考", "通过对话讨论正义、教育与理想社会。", "理想國|plato|republic", "https://www.gutenberg.org/ebooks/1497"),
        B("growth-franklin", "富兰克林自传", "本杰明·富兰克林", "心理与成长|历史", "一个人的学习、工作与生活回顾。", "富蘭克林自傳|benjaminfranklin", "https://www.gutenberg.org/ebooks/20203"),
        B("growth-james", "心理学原理", "威廉·詹姆斯", "心理与成长", "从心理学史中的经典认识注意与习惯。", "心理學原理|williamjames|principlesofpsychology", "https://www.gutenberg.org/ebooks/57628"),
        B("growth-atomic", "原子习惯", "詹姆斯·克利尔", "心理与成长", "从微小的重复行动理解习惯的形成。", "原子習慣|掌控习惯|掌控習慣|atomichabits|jamesclear", "https://jamesclear.com/atomic-habits"),
        B("growth-deep", "深度工作", "卡尔·纽波特", "心理与成长", "给需要思考的工作留出专注的时间。", "deepwork|calnewport|纽波特", "https://calnewport.com/deep-work-rules-for-focused-success-in-a-distracted-world/"),
        B("science-species", "物种起源", "查尔斯·达尔文", "自然科普", "回到进化思想的经典论述。", "物種起源|originofspecies|charlesdarwin|达尔文", "https://www.gutenberg.org/ebooks/1228"),
        B("science-voyage", "小猎犬号航海记", "查尔斯·达尔文", "自然科普|历史", "跟随实地观察，发现自然世界的细节。", "小獵犬號|voyageofthebeagle", "https://www.gutenberg.org/ebooks/944"),
        B("science-candle", "蜡烛的化学史", "迈克尔·法拉第", "自然科普", "从一支蜡烛出发，观察身边的科学。", "蠟燭的化學史|chemicalhistoryofacandle|michaelfaraday|法拉第", "https://www.gutenberg.org/ebooks/14474"),
        B("science-time", "时间简史", "史蒂芬·霍金", "自然科普", "从宇宙、时间和黑洞走进物理学的问题。", "時間簡史|briefhistoryoftime|stephenhawking|霍金", "https://www.hawking.org.uk/"),
    ];
    public static readonly DiscoveryBook[] Books = Prose.Concat(ComicCatalog.Books).ToArray();
}
