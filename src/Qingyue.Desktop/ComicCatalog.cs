namespace EpubKindleFix;

internal static class ComicCatalog
{
    private static DiscoveryBook M(string id, string title, string author, string topics, string introduction,
        string aliases, long cover, string work) => new("comic:" + id, title, author,
            new[] { "漫画" }.Concat(topics.Split('|', StringSplitOptions.RemoveEmptyEntries)).ToArray(),
            introduction, aliases.Split('|', StringSplitOptions.RemoveEmptyEntries), "https://openlibrary.org" + work)
        { Kind = "comic", CoverId = cover };

    // Series recommendations use a representative edition cover. Topic labels
    // and short introductions are editorial, and do not claim site availability.
    public static readonly DiscoveryBook[] Books =
    [
        M("onepiece", "海贼王", "尾田荣一郎", "幻想冒险", "和路飞与伙伴们出海，在漫长航程中追寻梦想与自由。",
            "航海王|海賊王|尾田栄一郎|尾田榮一郎|One Piece|Eiichiro Oda", 1020563, "/works/OL8215661W"),
        M("naruto", "火影忍者", "岸本齐史", "幻想冒险|心理与成长", "在忍者世界的试炼与羁绊中，看一个少年寻找自己的道路。",
            "火影|岸本斉史|岸本齊史|Naruto|Masashi Kishimoto", 7335243, "/works/OL13268171W"),
        M("spyfamily", "间谍过家家", "远藤达哉", "日常治愈|悬疑推理", "间谍、杀手与读心女孩组成临时家庭，秘密任务里也有温馨日常。",
            "間諜家家酒|间谍家家酒|間諜過家家|スパイファミリー|Spy x Family|Tatsuya Endo|遠藤達哉", 10193955, "/works/OL20873142W"),
        M("frieren", "葬送的芙莉莲", "山田钟人·阿部司", "幻想冒险|日常治愈", "冒险结束后，长寿的精灵重新上路，在相遇中理解时间与情感。",
            "葬送的芙莉蓮|葬送のフリーレン|Frieren|山田鐘人|アベツカサ", 11075074, "/works/OL24468510W"),
        M("fullmetal", "钢之炼金术师", "荒川弘", "幻想冒险|哲学思考", "兄弟二人踏上寻找失去之物的旅程，也面对等价交换的代价。",
            "鋼之鍊金術師|钢之炼金术士|鋼の錬金術師|Fullmetal Alchemist|Hiromu Arakawa", 863658, "/works/OL8756258W"),
        M("deathnote", "死亡笔记", "大场鸫·小畑健", "悬疑推理|哲学思考", "一本神秘笔记引发智力交锋，也把正义与权力的问题推到眼前。",
            "死亡筆記|Death Note|大場つぐみ|大場鶇|Tsugumi Ohba|Takeshi Obata", 1041608, "/works/OL8756309W"),
        M("conan", "名侦探柯南", "青山刚昌", "悬疑推理", "跟随变小的少年侦探，从现场细节与证词中寻找真相。",
            "名偵探柯南|名探偵コナン|柯南|Detective Conan|Case Closed|Gosho Aoyama|青山剛昌", 1787458, "/works/OL8400841W"),
        M("haikyu", "排球少年！！", "古馆春一", "运动竞技|心理与成长", "在排球场上认识队友与对手，一起练习、成长并挑战更高的目标。",
            "排球少年|ハイキュー|Haikyu|古舘春一|古館春一|Haruichi Furudate", 12352164, "/works/OL26403256W"),
        M("slamdunk", "灌篮高手", "井上雄彦", "运动竞技|恋爱青春", "从初次接触篮球开始，走进校园、队伍与比赛的热血青春。",
            "灌籃高手|Slam Dunk|井上雄彥|Takehiko Inoue", 991930, "/works/OL8783725W"),
        M("bluelock", "蓝色监狱", "金城宗幸·野村优介", "运动竞技|心理与成长", "年轻前锋在激烈竞争中寻找突破，思考个人能力与团队胜利。",
            "藍色監獄|Blue Lock|金城宗幸|野村優介|野村优介|Muneyuki Kaneshiro|Yusuke Nomura", 12552345, "/works/OL27042112W"),
        M("yotsuba", "四叶妹妹！", "东清彦", "日常治愈", "跟着好奇的小女孩观察身边的小事，在平凡日常里发现快乐。",
            "四葉妹妹|四叶妹妹|よつばと|Yotsuba|あずまきよひこ|Kiyohiko Azuma|東清彥", 1044564, "/works/OL8752663W"),
        M("kaguya", "辉夜大小姐想让我告白", "赤坂明", "恋爱青春|日常治愈", "两位不肯先告白的学生，把恋爱心事变成轻快的校园心理战。",
            "輝夜姬想讓人告白|辉夜姬想让人告白|輝夜大小姐想讓我告白|かぐや様は告らせたい|Kaguya-sama|Aka Akasaka|赤坂アカ", 13481074, "/works/OL20566643W"),
        M("drstone", "石纪元", "稻垣理一郎·Boichi", "科幻|自然科普|幻想冒险", "从石化后的世界重新出发，用科学与合作逐步重建生活。",
            "石紀元|新石纪|新石紀|Dr. Stone|Dr Stone|稲垣理一郎|Riichiro Inagaki", 14577228, "/works/OL37805541W"),
        M("titan", "进击的巨人", "谏山创", "幻想冒险|悬疑推理", "在城墙内外的危机与谜团中，追问自由、历史与选择的代价。",
            "進擊的巨人|進撃の巨人|Attack on Titan|Hajime Isayama|諫山創", 8471695, "/works/OL19357261W"),
        M("hunter", "全职猎人", "富坚义博", "幻想冒险", "少年出发寻找父亲，在猎人试炼与未知世界里结识伙伴。",
            "全職獵人|猎人|獵人|Hunter x Hunter|ハンター|Yoshihiro Togashi|冨樫義博|富樫義博", 7289483, "/works/OL17047535W"),
        M("demonslayer", "鬼灭之刃", "吾峠呼世晴", "幻想冒险|心理与成长", "为守护家人与同伴踏上旅途，在战斗中面对失去与成长。",
            "鬼滅之刃|鬼滅の刃|Demon Slayer|Koyoharu Gotouge|Koyoharu Gotoge", 9364005, "/works/OL19751404W")
    ];
}
