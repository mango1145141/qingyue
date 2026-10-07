export type BookState =
  | 'pending'
  | 'repairing'
  | 'ready'
  | 'sending'
  | 'accepted'
  | 'failed'
  | 'uncertain'
  | 'shared'
  | 'web'
  | 'large'
  | 'skipped'
  | 'needs-file';
export type LocalBook = {
  id: string;
  title: string;
  author: string;
  name: string;
  bytes: number;
  date: string;
  state: BookState;
  source?: Blob;
  repaired?: Blob;
  repairedName?: string;
  cover?: Blob;
  images?: number;
  added?: number;
  warnings?: string[];
  favorite: boolean;
  category: string;
  error?: string;
  mailJobId?: string;
  persisted?: boolean;
};
export type Wish = {
  id: string;
  title: string;
  author: string;
  kind: 'book' | 'comic';
  date: string;
};
const metadataKey = 'qingyue.mobile-shelf-meta.v1';
export function readLocal<T>(key: string, fallback: T): T {
  try {
    return JSON.parse(localStorage.getItem(key) || 'null') ?? fallback;
  } catch {
    return fallback;
  }
}
export function writeLocal(key: string, value: unknown) {
  localStorage.setItem(key, JSON.stringify(value));
}
let opening: Promise<IDBDatabase> | null = null;
function database() {
  if (!opening)
    opening = new Promise<IDBDatabase>((resolve, reject) => {
      if (!window.indexedDB) {
        reject(new Error('浏览器暂不支持保存书籍，请先下载修复版。'));
        return;
      }
      const request = indexedDB.open('qingyue-mobile-books', 1);
      request.onupgradeneeded = () =>
        request.result.createObjectStore('books', { keyPath: 'id' });
      request.onsuccess = () => {
        request.result.onversionchange = () => {
          request.result.close();
          opening = null;
        };
        resolve(request.result);
      };
      request.onerror = () => {
        opening = null;
        reject(new Error('无法读取本机书架。请允许网站储存数据。'));
      };
      request.onblocked = () => {
        opening = null;
        reject(new Error('请关闭其他轻阅页面后重试。'));
      };
    });
  return opening;
}
function metadata(book: LocalBook): LocalBook {
  const { source: _source, repaired: _repaired, cover: _cover, ...rest } = book;
  return rest;
}
function rememberMetadata(book: LocalBook) {
  const all = readLocal<LocalBook[]>(metadataKey, []);
  try {
    writeLocal(
      metadataKey,
      [metadata(book), ...all.filter((item) => item.id !== book.id)].slice(
        0,
        200,
      ),
    );
  } catch {
    /* IndexedDB remains authoritative when localStorage is full. */
  }
}
export async function saveBook(book: LocalBook) {
  const db = await database();
  await new Promise<void>((resolve, reject) => {
    const transaction = db.transaction('books', 'readwrite');
    transaction.objectStore('books').put({ ...book, persisted: true });
    transaction.oncomplete = () => resolve();
    transaction.onerror = transaction.onabort = () =>
      reject(
        new Error('本机空间不足，书籍暂未保存。请先下载修复版再离开页面。'),
      );
  });
  rememberMetadata(book);
}
export async function loadBooks(): Promise<LocalBook[]> {
  const db = await database();
  const records = await new Promise<LocalBook[]>((resolve, reject) => {
    const request = db
      .transaction('books', 'readonly')
      .objectStore('books')
      .getAll();
    request.onsuccess = () => resolve(request.result);
    request.onerror = () =>
      reject(new Error('本机书架读取失败，请刷新后重试。'));
  });
  const normalized = records.map(
    (book) =>
      ({
        ...book,
        persisted: true,
        state:
          book.state === 'repairing'
            ? 'pending'
            : book.state === 'sending'
              ? 'uncertain'
              : book.state,
      }) as LocalBook,
  );
  const known = new Set(normalized.map((book) => book.id));
  for (const old of readLocal<
    { id: string; title: string; date: string; added: number }[]
  >('qingyue.history', [])) {
    const id = 'legacy:' + old.id;
    if (!known.has(id))
      normalized.push({
        id,
        title: old.title,
        date: old.date,
        added: old.added,
        author: '',
        name: old.title + '.epub',
        bytes: 0,
        state: 'needs-file',
        favorite: false,
        category: '未分类',
        persisted: true,
      });
  }
  return normalized.sort((a, b) => b.date.localeCompare(a.date));
}
export function fallbackBooks() {
  return readLocal<LocalBook[]>(metadataKey, []).map(
    (book) => ({ ...book, state: 'needs-file', persisted: false }) as LocalBook,
  );
}
export async function deleteBook(id: string) {
  const db = await database();
  await new Promise<void>((resolve, reject) => {
    const transaction = db.transaction('books', 'readwrite');
    transaction.objectStore('books').delete(id);
    transaction.oncomplete = () => resolve();
    transaction.onerror = transaction.onabort = () =>
      reject(new Error('未能移除这本书，请重试。'));
  });
  try {
    writeLocal(
      metadataKey,
      readLocal<LocalBook[]>(metadataKey, []).filter((book) => book.id !== id),
    );
  } catch {
    /* Files have already been removed from the database. */
  }
  const old = readLocal<{ id: string }[]>('qingyue.history', []);
  writeLocal(
    'qingyue.history',
    old.filter((book) => 'legacy:' + book.id !== id),
  );
}
export async function clearBooks() {
  const db = await database();
  await new Promise<void>((resolve, reject) => {
    const transaction = db.transaction('books', 'readwrite');
    transaction.objectStore('books').clear();
    transaction.oncomplete = () => resolve();
    transaction.onerror = transaction.onabort = () =>
      reject(new Error('书架清理失败，请重试。'));
  });
  localStorage.removeItem(metadataKey);
  localStorage.removeItem('qingyue.history');
}
export const labels: Record<BookState, string> = {
  pending: '待检查',
  repairing: '检查中',
  ready: '待发送',
  sending: '邮件提交中',
  accepted: '邮件已提交',
  failed: '处理失败',
  uncertain: '发送待确认',
  shared: '已打开分享菜单',
  web: '网页发送待确认',
  large: '大文件待处理',
  skipped: '已跳过',
  'needs-file': '需重新选入文件',
};
export const unfinished = (book: LocalBook) =>
  !['accepted', 'shared', 'skipped', 'needs-file'].includes(book.state);
export const fileSize = (bytes: number) =>
  `${(bytes / 1024 / 1024).toFixed(bytes >= 1024 * 1024 ? 1 : 2)} MB`;
