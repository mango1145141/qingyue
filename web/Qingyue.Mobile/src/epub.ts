import JSZip from 'jszip';

export interface RepairResult {
  id?: string;
  file: File;
  author?: string;
  cover?: Blob;
  title: string;
  images: number;
  added: number;
  warnings: string[];
}

const media: Record<string, string> = {
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  gif: 'image/gif',
  svg: 'image/svg+xml',
  webp: 'image/webp',
  bmp: 'image/bmp',
};
const extension = (path: string) => path.split('.').pop()?.toLowerCase() || '';
const directory = (path: string) =>
  path.includes('/') ? path.slice(0, path.lastIndexOf('/') + 1) : '';
const elements = (doc: Document | Element, name: string) =>
  Array.from(doc.getElementsByTagNameNS('*', name));

function xml(text: string, label: string): Document {
  if (/<!DOCTYPE|<!ENTITY/i.test(text))
    throw new Error(`${label} 包含不支持的文档声明，请使用电脑版处理。`);
  const doc = new DOMParser().parseFromString(text, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length)
    throw new Error(`${label} 格式损坏，无法安全修复。`);
  return doc;
}

function resolve(base: string, reference: string): string | null {
  if (!reference || /^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(reference)) return null;
  let decoded: string;
  try {
    decoded = decodeURIComponent(reference.split(/[?#]/)[0]);
  } catch {
    return null;
  }
  const parts: string[] = [];
  for (const part of (decoded.startsWith('/')
    ? decoded.slice(1)
    : base + decoded
  ).split('/')) {
    if (part === '..') {
      if (!parts.length) return null;
      parts.pop();
    } else if (part && part !== '.') parts.push(part);
  }
  return parts.join('/');
}

function relative(base: string, target: string): string {
  const from = base.split('/').filter(Boolean);
  const to = target.split('/');
  while (from.length && from[0] === to[0]) {
    from.shift();
    to.shift();
  }
  return [...from.map(() => '..'), ...to].map(encodeURIComponent).join('/');
}

// Read central-directory sizes before decompression to keep mobile memory use bounded.
export function checkArchive(data: ArrayBuffer) {
  const v = new DataView(data);
  let end = -1;
  for (
    let p = data.byteLength - 22;
    p >= Math.max(0, data.byteLength - 65557);
    p--
  ) {
    if (
      v.getUint32(p, true) === 0x06054b50 &&
      p + 22 + v.getUint16(p + 20, true) === data.byteLength
    ) {
      end = p;
      break;
    }
  }
  if (end < 0) throw new Error('文件不是有效的 EPUB 压缩包。');
  const count = v.getUint16(end + 10, true);
  const centralSize = v.getUint32(end + 12, true);
  let p = v.getUint32(end + 16, true);
  if (
    v.getUint16(end + 4, true) ||
    v.getUint16(end + 6, true) ||
    count === 65535 ||
    p === 0xffffffff
  ) {
    throw new Error('此书使用不支持的分卷或大型压缩格式，请使用电脑版。');
  }
  if (count > 12000 || p + centralSize > end)
    throw new Error('书籍结构过大或损坏，请使用电脑版。');
  let total = 0;
  const names = new Set<string>();
  for (let i = 0; i < count; i++) {
    if (p + 46 > end || v.getUint32(p, true) !== 0x02014b50)
      throw new Error('书籍目录损坏。');
    const size = v.getUint32(p + 24, true);
    const nameSize = v.getUint16(p + 28, true);
    const next =
      p + 46 + nameSize + v.getUint16(p + 30, true) + v.getUint16(p + 32, true);
    if (next > end || v.getUint16(p + 8, true) & 1)
      throw new Error('此书损坏或已加密，无法修复。');
    const name = new TextDecoder().decode(
      data.slice(p + 46, p + 46 + nameSize),
    );
    if (names.has(name)) throw new Error('书籍存在重复文件，无法安全修复。');
    names.add(name);
    total += size;
    if (size > 32 * 1024 * 1024 || total > 180 * 1024 * 1024)
      throw new Error('展开后的书籍过大，请使用电脑版处理。');
    p = next;
  }
}

export async function repairEpub(
  file: File,
  progress: (value: number, label: string) => void,
): Promise<RepairResult> {
  if (!/\.epub$/i.test(file.name))
    throw new Error('请选择 .epub 格式的电子书。');
  if (file.size > 50 * 1024 * 1024)
    throw new Error('手机版支持 50 MB 以内的 EPUB，大文件请使用电脑版。');
  progress(5, '读取书籍');
  const data = await file.arrayBuffer();
  checkArchive(data);
  let source: JSZip;
  try {
    source = await JSZip.loadAsync(data, { checkCRC32: true });
  } catch {
    throw new Error('书籍无法解压或数据损坏，请重新下载。');
  }
  const names = Object.keys(source.files).filter(
    (name) => !source.files[name].dir,
  );
  if (
    names.some(
      (name) =>
        source.files[name].unsafeOriginalName &&
        source.files[name].unsafeOriginalName !== name,
    )
  ) {
    throw new Error('书籍包含异常文件路径，无法安全修复。');
  }
  const encryption = source.file('META-INF/encryption.xml');
  if (encryption) {
    const enc = xml(await encryption.async('string'), '加密声明');
    const allowed = [
      'http://www.idpf.org/2008/embedding',
      'http://ns.adobe.com/pdf/enc#RC',
    ];
    if (
      elements(enc, 'EncryptionMethod').some(
        (item) => !allowed.includes(item.getAttribute('Algorithm') || ''),
      )
    ) {
      throw new Error('此书受到加密保护，手机版不能修复。');
    }
  }
  const container = source.file('META-INF/container.xml');
  if (!container) throw new Error('这不是完整的 EPUB：缺少书籍目录。');
  const roots = elements(
    xml(await container.async('string'), '书籍目录'),
    'rootfile',
  );
  const opfPath =
    roots
      .find(
        (item) =>
          item.getAttribute('media-type') === 'application/oebps-package+xml',
      )
      ?.getAttribute('full-path') || roots[0]?.getAttribute('full-path');
  if (!opfPath || !source.file(opfPath))
    throw new Error('书籍内容清单不存在。');
  const doc = xml(await source.file(opfPath)!.async('string'), '内容清单');
  const manifest = elements(doc, 'manifest')[0];
  if (!manifest) throw new Error('书籍缺少资源清单，无法安全修复。');
  progress(30, '检查图片与内容清单');
  const base = directory(opfPath);
  const declared = new Set(
    elements(manifest, 'item').map((item) =>
      resolve(base, item.getAttribute('href') || ''),
    ),
  );
  const ids = new Set(
    Array.from(doc.querySelectorAll('[id]')).map((item) => item.id),
  );
  const images = names.filter((name) => media[extension(name)]);
  let added = 0;
  for (const image of images) {
    if (declared.has(image)) continue;
    let id = `kindle-image-${++added}`;
    while (ids.has(id)) id += '-new';
    ids.add(id);
    const item = doc.createElementNS(
      manifest.namespaceURI,
      manifest.prefix ? `${manifest.prefix}:item` : 'item',
    );
    item.setAttribute('id', id);
    item.setAttribute('href', relative(base, image));
    item.setAttribute('media-type', media[extension(image)]);
    manifest.appendChild(item);
  }
  const warnings: string[] = [];
  const missing = new Set<string>();
  for (const name of names.filter((n) =>
    /\.(?:xhtml|html|htm|css|svg)$/i.test(n),
  )) {
    const text = await source.file(name)!.async('string');
    const references = [
      ...text.matchAll(
        /(?:src|href)\s*=\s*["']([^"']+)["']|url\(\s*["']?([^\s"')]+)["']?\s*\)/gi,
      ),
    ];
    for (const ref of references) {
      const path = resolve(directory(name), ref[1] || ref[2]);
      if (path && media[extension(path)] && !source.file(path))
        missing.add(path);
    }
  }
  if (missing.size)
    warnings.push(
      `有 ${missing.size} 张引用图片缺少原文件，不能凭空恢复，建议重新下载此书。`,
    );
  if (images.some((name) => /\.webp$/i.test(name)))
    warnings.push('含 WebP 图片：已保留原图，部分 Kindle 可能无法正确显示。');
  const finalDeclared = new Set(
    elements(manifest, 'item').map((item) =>
      resolve(base, item.getAttribute('href') || ''),
    ),
  );
  if (images.some((name) => !finalDeclared.has(name)))
    throw new Error('图片清单校验失败。');
  const output = new JSZip();
  output.file('mimetype', 'application/epub+zip', {
    compression: 'STORE',
    createFolders: false,
  });
  const serialized = new XMLSerializer()
    .serializeToString(doc)
    .replace(/encoding\s*=\s*["'][^"']+["']/i, 'encoding="UTF-8"');
  for (const name of names) {
    if (name === 'mimetype') continue;
    const entry = source.file(name)!;
    output.file(
      name,
      name === opfPath ? serialized : await entry.async('uint8array'),
      {
        createFolders: false,
        date: entry.date,
        compression: 'DEFLATE',
      },
    );
  }
  progress(55, '生成修复版');
  const bytes = await output.generateAsync(
    {
      type: 'arraybuffer',
      compression: 'DEFLATE',
      compressionOptions: { level: 6 },
    },
    (meta) => {
      progress(Math.round(55 + meta.percent * 0.4), '生成修复版');
    },
  );
  const header = new DataView(bytes);
  const firstNameLength = header.getUint16(26, true);
  if (
    header.getUint32(0, true) !== 0x04034b50 ||
    header.getUint16(8, true) !== 0 ||
    new TextDecoder().decode(bytes.slice(30, 30 + firstNameLength)) !==
      'mimetype' ||
    header.getUint16(28, true) !== 0
  ) {
    throw new Error('修复版打包检查未通过，请使用电脑版。');
  }
  progress(100, '修复完成');
  return {
    file: new File([bytes], file.name.replace(/\.epub$/i, '-修复版.epub'), {
      type: 'application/epub+zip',
    }),
    title:
      elements(doc, 'title')[0]?.textContent?.trim() ||
      file.name.replace(/\.epub$/i, ''),
    author: elements(doc, 'creator')
      .map((item) => item.textContent?.trim())
      .filter(Boolean)
      .join('、'),
    cover: await coverBlob(source, manifest, doc, base),
    images: images.length,
    added,
    warnings,
  };
}

export async function sampleBook(): Promise<File> {
  const zip = new JSZip();
  zip.file('mimetype', 'application/epub+zip', { compression: 'STORE' });
  zip.file(
    'META-INF/container.xml',
    '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>',
  );
  zip.file(
    'EPUB/book.opf',
    '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:uuid:little-reading-example</dc:identifier><dc:title>轻阅使用示例</dc:title><dc:language>zh</dc:language><meta property="dcterms:modified">2026-10-04T00:00:00Z</meta></metadata><manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest><spine><itemref idref="chapter"/></spine></package>',
  );
  zip.file(
    'EPUB/chapter.xhtml',
    '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>轻阅</title></head><body><h1>把时间留给阅读</h1><p>这是轻阅自动生成的示例书。它刻意漏登记一张插图，供你体验检查与修复。</p><img src="illustration.svg" alt="一本打开的书"/></body></html>',
  );
  zip.file(
    'EPUB/nav.xhtml',
    '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>目录</title></head><body><nav epub:type="toc"><ol><li><a href="chapter.xhtml">把时间留给阅读</a></li></ol></nav></body></html>',
  );
  zip.file(
    'EPUB/illustration.svg',
    '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200"><rect width="320" height="200" fill="#f2f5fa"/><path d="M160 65 Q110 40 65 60 V150 Q110 130 160 150 Q210 130 255 150 V60 Q210 40 160 65Z" fill="#6b8baf"/></svg>',
  );
  return new File(
    [await zip.generateAsync({ type: 'arraybuffer' })],
    '轻阅使用示例.epub',
    { type: 'application/epub+zip' },
  );
}

async function coverBlob(
  zip: JSZip,
  manifest: Element,
  doc: Document,
  base: string,
) {
  const coverId = elements(doc, 'meta')
    .find((item) => item.getAttribute('name') === 'cover')
    ?.getAttribute('content');
  const item = elements(manifest, 'item').find(
    (item) =>
      item.getAttribute('properties')?.split(/\s+/).includes('cover-image') ||
      (coverId && item.id === coverId),
  );
  const path = resolve(base, item?.getAttribute('href') || '');
  if (!path || !/\.(?:jpe?g|png|gif|webp)$/i.test(path) || !zip.file(path))
    return undefined;
  const bytes = await zip.file(path)!.async('uint8array');
  if (bytes.byteLength > 4 * 1024 * 1024) return undefined;
  return new Blob([new Uint8Array(bytes)], { type: media[extension(path)] });
}
export async function epubPreview(file: Blob) {
  if (file.size > 50 * 1024 * 1024) throw new Error('大文件请在电脑版预览。');
  const buffer = await file.arrayBuffer();
  checkArchive(buffer);
  const zip = await JSZip.loadAsync(buffer);
  const container = zip.file('META-INF/container.xml');
  if (!container) throw new Error('书籍目录不存在。');
  const path = elements(
    xml(await container.async('string'), '目录'),
    'rootfile',
  )[0]?.getAttribute('full-path');
  if (!path || !zip.file(path)) throw new Error('内容清单不存在。');
  const doc = xml(await zip.file(path)!.async('string'), '内容清单');
  const items = elements(doc, 'item');
  const chapters = elements(doc, 'itemref')
    .slice(0, 1000)
    .map((ref, index) => {
      const item = items.find((item) => item.id === ref.getAttribute('idref'));
      return {
        title: '第 ' + (index + 1) + ' 节',
        path: resolve(directory(path), item?.getAttribute('href') || ''),
      };
    })
    .filter((item) => item.path && zip.file(item.path));
  if (!chapters.length) throw new Error('暂未找到可预览的章节。');
  const ncxItem = items.find(
    (item) => item.getAttribute('media-type') === 'application/x-dtbncx+xml',
  );
  const ncxPath = resolve(directory(path), ncxItem?.getAttribute('href') || '');
  if (ncxPath && zip.file(ncxPath)) {
    try {
      for (const point of elements(
        xml(await zip.file(ncxPath)!.async('string'), '目录'),
        'navPoint',
      )) {
        const target = resolve(
          directory(ncxPath),
          elements(point, 'content')[0]?.getAttribute('src') || '',
        );
        const chapter = chapters.find((item) => item.path === target);
        const title = elements(point, 'text')[0]?.textContent?.trim();
        if (chapter && title) chapter.title = title;
      }
    } catch {
      /* Optional navigation cannot block text preview. */
    }
  }
  return {
    chapters,
    async read(index: number) {
      const entry = zip.file(chapters[index]?.path || '');
      if (!entry) throw new Error('此章节不存在。');
      let text = await entry.async('string');
      if (/<!ENTITY/i.test(text))
        throw new Error('此章节包含不支持的文档声明。');
      text = text.replace(/<!DOCTYPE[^>]*>/gi, '');
      const chapter = new DOMParser().parseFromString(
        text,
        'application/xhtml+xml',
      );
      if (chapter.getElementsByTagName('parsererror').length)
        throw new Error('此章节暂不能安全预览。');
      for (const tag of ['script', 'style', 'iframe', 'object'])
        elements(chapter, tag).forEach((node) => node.remove());
      const title =
        elements(chapter, 'h1')[0]?.textContent ||
        elements(chapter, 'h2')[0]?.textContent ||
        elements(chapter, 'title')[0]?.textContent;
      const paragraphs =
        elements(chapter, 'body')[0]?.textContent?.trim() || '';
      return {
        title: title?.trim() || chapters[index].title,
        text: paragraphs.slice(0, 80000),
        truncated: paragraphs.length > 80000,
      };
    },
  };
}
