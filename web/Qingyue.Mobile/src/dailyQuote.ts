const quotes: [string, string, string][] = [
  [
    '学而时习之，不亦说乎？',
    '孔子 ·《论语·学而》',
    'https://ctext.org/analects/xue-er/zh',
  ],
  [
    '温故而知新，可以为师矣。',
    '孔子 ·《论语·为政》',
    'https://ctext.org/analects/wei-zheng/zh',
  ],
  [
    '学而不思则罔，思而不学则殆。',
    '孔子 ·《论语·为政》',
    'https://ctext.org/analects/wei-zheng/zh',
  ],
  [
    '知之为知之，不知为不知，是知也。',
    '孔子 ·《论语·为政》',
    'https://ctext.org/analects/wei-zheng/zh',
  ],
  [
    '学而不厌，诲人不倦。',
    '孔子 ·《论语·述而》节选',
    'https://ctext.org/lunyu-zhushu/shu-er',
  ],
  ['学不可以已。', '荀子 ·《荀子·劝学》', 'https://ctext.org/xunzi/quan-xue'],
  [
    '青，取之于蓝，而青于蓝。',
    '荀子 ·《荀子·劝学》',
    'https://ctext.org/xunzi/quan-xue',
  ],
  [
    '锲而不舍，金石可镂。',
    '荀子 ·《荀子·劝学》',
    'https://ctext.org/xunzi/quan-xue',
  ],
  [
    '不积跬步，无以至千里；不积小流，无以成江海。',
    '荀子 ·《荀子·劝学》节选',
    'https://ctext.org/xunzi/quan-xue',
  ],
  [
    '千里之行，始于足下。',
    '老子 ·《道德经》第六十四章',
    'https://ctext.org/dao-de-jing/zh',
  ],
  [
    '知人者智，自知者明。',
    '老子 ·《道德经》第三十三章',
    'https://ctext.org/dao-de-jing/zh',
  ],
  ['上善若水。', '老子 ·《道德经》第八章', 'https://ctext.org/dao-de-jing'],
  [
    '知者不言，言者不知。',
    '老子 ·《道德经》第五十六章',
    'https://ctext.org/dao-de-jing',
  ],
  [
    '不登高山，不知天之高也。',
    '荀子 ·《荀子·劝学》节选',
    'https://ctext.org/xunzi/quan-xue',
  ],
  [
    '不临深溪，不知地之厚也。',
    '荀子 ·《荀子·劝学》节选',
    'https://ctext.org/xunzi/quan-xue',
  ],
  [
    '读书破万卷，下笔如有神。',
    '杜甫 ·《奉赠韦左丞丈二十二韵》',
    'https://dict.revised.moe.edu.tw/dictView.jsp?ID=47463&q=1&word=%E8%AE%80%E6%9B%B8',
  ],
  [
    '问渠那得清如许？为有源头活水来。',
    '朱熹 ·《观书有感二首·其一》',
    'https://www.shidianguji.com/zh/mingju/7624424286379982888',
  ],
  [
    '奇文共欣赏，疑义相与析。',
    '陶渊明 ·《移居二首·其一》',
    'https://dict.variants.moe.edu.tw/dictView.jsp?ID=43234',
  ],
  [
    '纸上得来终觉浅，绝知此事要躬行。',
    '陆游 ·《冬夜读书示子聿》',
    'https://www.tcps.ntpc.edu.tw/var/file/0/1000/img/135/573725261.pdf',
  ],
];

export function dailyQuote() {
  const day = Math.floor((Date.now() + 8 * 3600000) / 86400000) + 719162;
  return quotes[day % quotes.length];
}
