import {
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type ReactNode,
} from 'react';
import {
  ArrowUpRight,
  BookOpen,
  Check,
  ChevronRight,
  Download,
  Heart,
  Library,
  ListOrdered,
  Mail,
  MoreHorizontal,
  Plus,
  Search,
  Settings,
  Share2,
  Star,
  Trash2,
  Upload,
  X,
} from 'lucide-react';
import { repairEpub, sampleBook, epubPreview, type RepairResult } from './epub';
import { useSettingsSync, defaults, type SharedSettings } from './settingsSync';
import { useCloudMail } from './cloudMail';
import {
  loadBooks,
  fallbackBooks,
  saveBook,
  deleteBook,
  clearBooks,
  readLocal,
  writeLocal,
  labels,
  unfinished,
  fileSize,
  type LocalBook,
  type Wish,
} from './mobileLibrary';
import { dailyQuote } from './dailyQuote';
import SettingsPanel from './SettingsPanel';

type Page = 'home' | 'shelf' | 'queue' | 'wishes';
type Kind = 'book' | 'comic';
type Appearance = {
  theme: 'system' | 'light' | 'dark';
  large: boolean;
  reduced: boolean;
};
type Modal =
  | { kind: 'settings' | 'library' | 'help' | 'clear' }
  | { kind: 'book' | 'preview' | 'remove'; id: string };
const amazon = 'https://www.amazon.com/sendtokindle';
const sites = { book: 'https://zh.z-library.sk/', comic: 'https://koz.moe/' };
const searchUrl = (kind: Kind, query: string) =>
  kind === 'book'
    ? sites.book + 's/?q=' + encodeURIComponent(query.trim())
    : sites.comic + 'list.php?s=' + encodeURIComponent(query.trim());
const fault = (e: unknown) =>
  e instanceof Error ? e.message : '操作暂未完成，请重试。';
const fileOf = (book: LocalBook) =>
  new File([book.repaired!], book.repairedName || book.name, {
    type: 'application/epub+zip',
  });
const initialSettings = () => ({
  ...defaults,
  ...readLocal('qingyue.shared-settings', {}),
  ...readLocal('qingyue.preferences', {}),
});
const nav = [
  { id: 'home', text: '导入', icon: Upload },
  { id: 'shelf', text: '书架', icon: Library },
  { id: 'queue', text: '队列', icon: ListOrdered },
  { id: 'wishes', text: '想读', icon: Heart },
] as const;

function Sheet({
  title,
  children,
  close,
}: {
  title: string;
  children: ReactNode;
  close: () => void;
}) {
  const panel = useRef<HTMLDivElement>(null);
  const closeRef = useRef(close);
  closeRef.current = close;
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const old = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    panel.current?.querySelector<HTMLElement>('button, input, a')?.focus();
    function key(event: KeyboardEvent) {
      if (event.key === 'Escape') closeRef.current();
      if (event.key !== 'Tab') return;
      const items = Array.from(
        panel.current?.querySelectorAll<HTMLElement>(
          'button:not(:disabled),input:not(:disabled),select,textarea,a[href]',
        ) || [],
      ).filter((item) => item.getClientRects().length);
      if (!items.length) return;
      const first = items[0],
        last = items[items.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      }
      if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }
    document.addEventListener('keydown', key);
    return () => {
      document.body.style.overflow = old;
      document.removeEventListener('keydown', key);
      if (previous?.isConnected) previous.focus();
    };
  }, []);
  return (
    <div
      className="scrim"
      onClick={(event) => {
        if (event.target === event.currentTarget) close();
      }}
    >
      <div
        className="sheet"
        role="dialog"
        aria-modal="true"
        aria-labelledby="sheet-title"
        ref={panel}
      >
        <div className="sheet-head">
          <h2 id="sheet-title">{title}</h2>
          <button className="icon-button" aria-label="关闭" onClick={close}>
            <X size={22} />
          </button>
        </div>
        <div className="sheet-body">{children}</div>
      </div>
    </div>
  );
}
function Cover({ book }: { book: LocalBook }) {
  const [url, setUrl] = useState('');
  useEffect(() => {
    if (!book.cover) {
      setUrl('');
      return;
    }
    const next = URL.createObjectURL(book.cover);
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [book.cover]);
  return (
    <div className="cover">
      {url ? (
        <img src={url} alt={book.title + ' 封面'} />
      ) : (
        <>
          <BookOpen size={24} />
          <span>{book.title}</span>
        </>
      )}
    </div>
  );
}
function Preview({ book }: { book: LocalBook }) {
  const [reader, setReader] = useState<Awaited<
    ReturnType<typeof epubPreview>
  > | null>(null);
  const [chapter, setChapter] = useState(0);
  const [content, setContent] = useState<{
    title: string;
    text: string;
    truncated: boolean;
  } | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let current = true;
    if (!book.repaired) {
      setError('请先修复书籍后再预览。');
      return;
    }
    void epubPreview(book.repaired)
      .then((next) => {
        if (current) setReader(next);
      })
      .catch((e) => {
        if (current) setError(fault(e));
      });
    return () => {
      current = false;
    };
  }, [book.repaired]);
  useEffect(() => {
    if (!reader) return;
    let current = true;
    setContent(null);
    setError('');
    void reader
      .read(chapter)
      .then((next) => {
        if (current) setContent(next);
      })
      .catch((e) => {
        if (current) setError(fault(e));
      });
    return () => {
      current = false;
    };
  }, [reader, chapter]);
  return (
    <div className="reader">
      <p className="subtle">目录与文字预览 · 完整排版与插画请在 Kindle 阅读</p>
      {reader && (
        <label className="field-label">
          选择章节
          <select
            value={chapter}
            onChange={(e) => setChapter(Number(e.target.value))}
          >
            {reader.chapters.map((item, i) => (
              <option key={i} value={i}>
                {item.title}
              </option>
            ))}
          </select>
        </label>
      )}
      {error ? (
        <p className="error" role="alert">
          {error}
        </p>
      ) : content ? (
        <>
          <h3>{content.title}</h3>
          <div className="chapter-text">{content.text}</div>
          {content.truncated && (
            <p className="subtle">本章较长，预览显示前 8 万字。</p>
          )}
        </>
      ) : (
        <p role="status">正在打开章节…</p>
      )}
    </div>
  );
}
function BookDetails({
  book,
  update,
  remove,
  preview,
}: {
  book: LocalBook;
  update: (patch: Partial<LocalBook>) => void;
  remove: () => void;
  preview: () => void;
}) {
  const [title, setTitle] = useState(book.title),
    [author, setAuthor] = useState(book.author),
    [category, setCategory] = useState(book.category);
  const [saved, setSaved] = useState(false);
  return (
    <>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (!title.trim()) return;
          update({
            title: title.trim(),
            author: author.trim(),
            category: category.trim() || '未分类',
          });
          setSaved(true);
        }}
      >
        <label className="field-label">
          书名
          <input
            required
            maxLength={200}
            value={title}
            onChange={(e) => {
              setSaved(false);
              setTitle(e.target.value);
            }}
          />
        </label>
        <label className="field-label">
          作者
          <input
            maxLength={100}
            value={author}
            onChange={(e) => {
              setSaved(false);
              setAuthor(e.target.value);
            }}
          />
        </label>
        <label className="field-label">
          分类
          <input
            maxLength={40}
            value={category}
            onChange={(e) => {
              setSaved(false);
              setCategory(e.target.value);
            }}
          />
        </label>
        <button className="primary full" type="submit">
          {saved ? '已保存' : '保存书籍信息'}
        </button>
      </form>
      <p className="subtle">这些信息只用于本机书架，不修改 EPUB 原内容。</p>
      <button
        className={'secondary full ' + (book.favorite ? 'selected' : '')}
        onClick={() => update({ favorite: !book.favorite })}
      >
        <Star size={18} />
        {book.favorite ? '已收藏 · 点击取消' : '收藏这本书'}
      </button>
      <button
        className="secondary full"
        disabled={!book.repaired}
        onClick={preview}
      >
        <BookOpen size={18} />
        目录与文字预览
      </button>
      <button className="text-button danger" onClick={remove}>
        <Trash2 size={17} />
        移除这本书
      </button>
    </>
  );
}
export default function App() {
  const [page, setPage] = useState<Page>('home');
  const [modal, setModal] = useState<Modal | null>(() =>
    location.hash.includes('pair=') ? { kind: 'settings' } : null,
  );
  const [notice, setNotice] = useState('');
  const [shared, setShared] = useState<SharedSettings>(initialSettings);
  const [appearance, setAppearance] = useState<Appearance>(() =>
    readLocal('qingyue.appearance', {
      theme: 'system',
      large: false,
      reduced: false,
    }),
  );
  const [books, setBooks] = useState<LocalBook[]>([]);
  const booksRef = useRef<LocalBook[]>([]);
  const [loading, setLoading] = useState(true);
  const running = useRef(false);
  const writes = useRef(new Map<string, Promise<void>>());
  const [paused, setPaused] = useState(() =>
    readLocal('qingyue.queue-paused', false),
  );
  const [manualRun, setManualRun] = useState(false);
  const [progress, setProgress] = useState({ id: '', percent: 0, text: '' });
  const fileInput = useRef<HTMLInputElement>(null);
  const activeMail = useRef<string | null>(null);
  const [activeId, setActiveId] = useState('');
  const [kind, setKind] = useState<Kind>('book');
  const [queries, setQueries] = useState<Record<Kind, string>>(() =>
    readLocal('qingyue.search-queries', {
      book: readLocal('qingyue.library-query', ''),
      comic: '',
    }),
  );
  const [recent, setRecent] = useState<{ kind: Kind; query: string }[]>(() =>
    readLocal('qingyue.mobile-search-history', []),
  );
  const composing = useRef(false);
  const [wishes, setWishes] = useState<Wish[]>(() =>
    readLocal('qingyue.mobile-wishes', []),
  );
  const [wishDraft, setWishDraft] = useState({
    title: '',
    author: '',
    kind: 'book' as Kind,
  });
  const [filter, setFilter] = useState('');
  const [favorites, setFavorites] = useState(false);
  const [categoryFilter, setCategoryFilter] = useState('全部');
  const [quote, setQuote] = useState(dailyQuote);
  function local(key: string, value: unknown) {
    try {
      writeLocal(key, value);
      return true;
    } catch {
      setNotice('浏览器未能保存更改，请允许网站储存数据。');
      return false;
    }
  }
  const sync = useSettingsSync(shared, (next) => {
    setShared(next);
    local('qingyue.shared-settings', next);
    local('qingyue.preferences', {
      autoRepair: next.autoRepair,
      rememberHistory: next.rememberHistory,
    });
  });
  const mail = useCloudMail(
    sync.device,
    shared.senderEmail,
    shared.kindleEmail,
  );
  useEffect(() => {
    if (!mail.sending && mail.message) setNotice(mail.message);
  }, [mail.message, mail.sending]);
  function updateSettings(patch: Partial<SharedSettings>) {
    const next = { ...shared, ...patch };
    setShared(next);
    local('qingyue.shared-settings', next);
    local('qingyue.preferences', {
      autoRepair: next.autoRepair,
      rememberHistory: next.rememberHistory,
    });
    sync.queue(patch);
  }
  function updateAppearance(patch: Partial<Appearance>) {
    const next = { ...appearance, ...patch };
    setAppearance(next);
    local('qingyue.appearance', next);
  }
  useEffect(() => {
    document.documentElement.dataset.theme = appearance.theme;
    document.documentElement.dataset.large = String(appearance.large);
    document.documentElement.dataset.reduced = String(appearance.reduced);
  }, [appearance]);
  useEffect(() => {
    const update = () => setQuote(dailyQuote());
    const timer = setInterval(update, 60000);
    document.addEventListener('visibilitychange', update);
    return () => {
      clearInterval(timer);
      document.removeEventListener('visibilitychange', update);
    };
  }, []);
  useEffect(() => {
    let alive = true;
    void loadBooks()
      .then((next) => {
        if (alive) {
          booksRef.current = next;
          setBooks(next);
          setLoading(false);
        }
      })
      .catch((e) => {
        if (alive) {
          const next = fallbackBooks();
          booksRef.current = next;
          setBooks(next);
          setLoading(false);
          setNotice(fault(e));
        }
      });
    return () => {
      alive = false;
    };
  }, []);
  function remember(book: LocalBook) {
    if (!shared.rememberHistory && !book.persisted) return;
    const previous = writes.current.get(book.id) || Promise.resolve();
    const next = previous
      .catch(() => {})
      .then(() => saveBook(book))
      .then(() => {
        // Only mark saved if the record still exists; never bring a removed book back.
        const latest = booksRef.current.find((item) => item.id === book.id);
        if (latest) {
          booksRef.current = booksRef.current.map((item) =>
            item.id === book.id ? { ...item, persisted: true } : item,
          );
          setBooks(booksRef.current);
        }
      })
      .catch((e) => setNotice(fault(e)));
    writes.current.set(book.id, next);
  }
  function updateBook(id: string, patch: Partial<LocalBook>) {
    const previous = booksRef.current.find((book) => book.id === id);
    if (!previous) return;
    const next = { ...previous, ...patch };
    booksRef.current = booksRef.current.map((book) =>
      book.id === id ? next : book,
    );
    setBooks(booksRef.current);
    remember(next);
  }
  function admit(files: File[]) {
    const accepted: LocalBook[] = [],
      rejected: string[] = [];
    for (const file of files) {
      if (!/\.epub$/i.test(file.name)) {
        rejected.push(file.name + '：请选择 EPUB 文件。');
        continue;
      }
      if (file.size > 200 * 1024 * 1024) {
        rejected.push(file.name + '：超过 200 MB。');
        continue;
      }
      if (!file.size) {
        rejected.push(file.name + '：文件为空。');
        continue;
      }
      accepted.push({
        id: crypto.randomUUID(),
        title: file.name.replace(/\.epub$/i, ''),
        author: '',
        name: file.name,
        bytes: file.size,
        date: new Date().toISOString(),
        state: file.size > 50 * 1024 * 1024 ? 'large' : 'pending',
        source: file,
        favorite: false,
        category: '未分类',
        persisted: false,
      });
    }
    if (accepted.length) {
      booksRef.current = [...accepted, ...booksRef.current];
      setBooks(booksRef.current);
      accepted.forEach(remember);
      setActiveId(accepted[0].id);
      setPage('home');
    }
    if (rejected.length) setNotice(rejected.join(' '));
  }
  useEffect(() => {
    if (
      loading ||
      paused ||
      running.current ||
      (!shared.autoRepair && !manualRun)
    )
      return;
    const pending = booksRef.current
      .filter((book) => book.state === 'pending' && book.source)
      .sort((a, b) => a.date.localeCompare(b.date))[0];
    if (!pending) {
      if (manualRun) setManualRun(false);
      return;
    }
    running.current = true;
    updateBook(pending.id, { state: 'repairing', error: undefined });
    void repairEpub(
      new File([pending.source!], pending.name, {
        type: 'application/epub+zip',
      }),
      (percent, text) => setProgress({ id: pending.id, percent, text }),
    )
      .then((result) =>
        updateBook(pending.id, {
          state: 'ready',
          title: result.title,
          author: result.author || pending.author,
          repaired: result.file,
          repairedName: result.file.name,
          cover: result.cover,
          images: result.images,
          added: result.added,
          warnings: result.warnings,
        }),
      )
      .catch((e) =>
        updateBook(pending.id, { state: 'failed', error: fault(e) }),
      )
      .finally(() => {
        running.current = false;
        setProgress((previous) => ({ ...previous, id: '' }));
      });
  }, [books, loading, shared.autoRepair, paused, manualRun, progress.id]);
  function pause(value: boolean) {
    setPaused(value);
    local('qingyue.queue-paused', value);
    if (!value) setManualRun(true);
  }
  useEffect(() => {
    if (activeMail.current && mail.job) {
      updateBook(activeMail.current, {
        state: mail.job.status === 'uploading' ? 'sending' : mail.job.status,
        mailJobId: mail.job.jobId,
        error:
          mail.job.status === 'failed' || mail.job.status === 'uncertain'
            ? mail.job.message
            : undefined,
      });
    }
  }, [mail.job]);
  async function send(book: LocalBook) {
    if (mail.sending || mail.working || activeMail.current || !book.repaired)
      return;
    if (!sync.connected || !mail.connection?.connected) {
      setNotice('请先在设置里连接发件邮箱。');
      setModal({ kind: 'settings' });
      return;
    }
    activeMail.current = book.id;
    updateBook(book.id, { state: 'sending', error: undefined });
    try {
      await sync.flush();
      const result: RepairResult = {
        id: book.id,
        file: fileOf(book),
        title: book.title,
        images: book.images || 0,
        added: book.added || 0,
        warnings: book.warnings || [],
      };
      const job = await mail.send(result);
      if (job)
        updateBook(book.id, {
          state: job.status === 'uploading' ? 'uncertain' : job.status,
          mailJobId: job.jobId,
          error: ['failed', 'uncertain'].includes(job.status)
            ? job.message
            : undefined,
        });
      else
        updateBook(book.id, { state: book.mailJobId ? 'uncertain' : 'ready' });
    } catch (e) {
      updateBook(book.id, { state: 'uncertain', error: fault(e) });
    } finally {
      activeMail.current = null;
    }
  }
  async function check(book: LocalBook) {
    if (!book.mailJobId || mail.sending || mail.working) return;
    activeMail.current = book.id;
    try {
      const job = await mail.check(book.mailJobId);
      if (job)
        updateBook(book.id, {
          state: job.status === 'uploading' ? 'ready' : job.status,
          error: job.message,
        });
      else setNotice('暂未查到最新结果，请稍后再查询。');
    } finally {
      activeMail.current = null;
    }
  }
  function download(book: LocalBook) {
    if (!book.repaired) return;
    const url = URL.createObjectURL(book.repaired);
    const a = document.createElement('a');
    a.href = url;
    a.download = book.repairedName || book.name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    setNotice('已请求下载修复版，请在 Safari 下载或「文件」中查看。');
  }
  async function share(book: LocalBook) {
    if (!book.repaired) return;
    const file = fileOf(book);
    if (!navigator.share || !navigator.canShare?.({ files: [file] })) {
      setNotice(
        '此浏览器不支持文件分享。请先保存修复版，在「文件」中长按书籍，选择分享 → Kindle；也可使用 Amazon 网页发送。',
      );
      return;
    }
    try {
      await navigator.share({ files: [file], title: book.title });
      updateBook(book.id, { state: 'shared' });
      setNotice('分享菜单已完成操作，请在 Kindle App 确认是否发送成功。');
    } catch (e) {
      setNotice(
        (e as Error).name === 'AbortError'
          ? '已取消分享，修复版仍保留。'
          : fault(e),
      );
    }
  }
  function rememberSearch(searchKind: Kind, query: string) {
    const next = [
      { kind: searchKind, query: query.trim() },
      ...recent.filter(
        (item) => item.kind !== searchKind || item.query !== query.trim(),
      ),
    ].slice(0, 30);
    setRecent(next);
    local('qingyue.mobile-search-history', next);
  }
  function search(event: FormEvent) {
    if (composing.current) {
      event.preventDefault();
      return;
    }
    if (!queries[kind].trim()) {
      event.preventDefault();
      setNotice('请先输入书名、作者或 ISBN。');
      return;
    }
    rememberSearch(kind, queries[kind]);
    setNotice('已携带关键词打开书库。下载 EPUB 后，回到轻阅导入。');
  }
  function setQuery(query: string) {
    const next = { ...queries, [kind]: query };
    setQueries(next);
    local('qingyue.search-queries', next);
  }
  function saveWishes(next: Wish[]) {
    if (!local('qingyue.mobile-wishes', next)) return;
    setWishes(next);
  }
  function toggleWish(title: string, wishKind: Kind, author = '') {
    if (!title.trim()) {
      setNotice('请先输入书名。');
      return;
    }
    const known = wishes.find(
      (item) => item.title === title.trim() && item.kind === wishKind,
    );
    saveWishes(
      known
        ? wishes.filter((item) => item.id !== known.id)
        : [
            {
              id: crypto.randomUUID(),
              title: title.trim(),
              author: author.trim(),
              kind: wishKind,
              date: new Date().toISOString(),
            },
            ...wishes,
          ],
    );
    setNotice(known ? '已从想读中移除' : '已加入想读');
  }
  async function remove(id: string) {
    const book = booksRef.current.find((item) => item.id === id);
    if (!book || ['repairing', 'sending'].includes(book.state)) return;
    try {
      await writes.current.get(id);
      if (book.persisted || id.startsWith('legacy:')) await deleteBook(id);
      booksRef.current = booksRef.current.filter((item) => item.id !== id);
      setBooks(booksRef.current);
      writes.current.delete(id);
      setModal(null);
      setNotice('已从本机书架移除。');
    } catch (e) {
      setNotice(fault(e));
    }
  }
  async function clear() {
    if (running.current || mail.sending) {
      setNotice('请等待当前处理完成后再清空书架。');
      return;
    }
    try {
      await Promise.all(writes.current.values());
      await clearBooks();
      booksRef.current = [];
      setBooks([]);
      writes.current.clear();
      setModal(null);
      setNotice('本机书架已清空，想读和邮箱设置仍保留。');
    } catch (e) {
      setNotice(fault(e));
    }
  }
  const active = books.find((book) => book.id === activeId);
  const queue = books.filter(unfinished);
  const categories = [...new Set(books.map((book) => book.category))];
  const filtered = books.filter(
    (book) =>
      (book.title + book.author).toLowerCase().includes(filter.toLowerCase()) &&
      (!favorites || book.favorite) &&
      (categoryFilter === '全部' || book.category === categoryFilter),
  );
  const wished = wishes.some(
    (item) => item.title === queries[kind].trim() && item.kind === kind,
  );
  const modalBook =
    modal && 'id' in modal
      ? books.find((book) => book.id === modal.id)
      : undefined;
  function actions(book: LocalBook) {
    const busy = ['repairing', 'sending'].includes(book.state);
    return (
      <div className="book-actions">
        {book.state === 'pending' && (
          <button className="primary full" onClick={() => pause(false)}>
            检查并修复
          </button>
        )}
        {book.state === 'repairing' && (
          <div className="progress-wrap" role="status">
            <span>
              {progress.id === book.id ? progress.text : '检查中'}{' '}
              {progress.id === book.id ? progress.percent : 0}%
            </span>
            <progress
              max="100"
              value={progress.id === book.id ? progress.percent : 0}
            />
          </div>
        )}
        {book.state === 'failed' && !book.repaired && book.source && (
          <button
            className="secondary full"
            onClick={() => {
              updateBook(book.id, { state: 'pending', error: undefined });
              pause(false);
            }}
          >
            重新检查
          </button>
        )}
        {book.state === 'needs-file' && (
          <>
            <p className="subtle">这是旧版书名记录，请重新选入文件。</p>
            <button
              className="secondary full"
              onClick={() => fileInput.current?.click()}
            >
              重新选择书籍
            </button>
          </>
        )}
        {book.state === 'large' && (
          <div className="large-note">
            <strong>原书 {fileSize(book.bytes)}</strong>
            <p>
              手机本地修复上限为 50 MB。50–200 MB 文件请先用电脑版检查，再在
              Amazon 网页上传；需登录 Amazon 并确认发送。
            </p>
            <a
              className="secondary full"
              href={amazon}
              target="_blank"
              rel="noopener noreferrer"
              onClick={() => updateBook(book.id, { state: 'web' })}
            >
              打开 Amazon 发送 <ArrowUpRight size={17} />
            </a>
          </div>
        )}
        {book.state === 'web' && !book.repaired && (
          <p className="subtle">
            已打开网页，需在 Amazon 确认上传与发送。轻阅无法确认外站发送结果。
          </p>
        )}
        {book.repaired && (
          <>
            <div className="repair-report">
              <span>
                <Check size={15} />
                已检查 {book.images || 0} 张图片
              </span>
              <span>补齐 {book.added || 0} 处登记</span>
            </div>
            {book.warnings?.map((warning) => (
              <p className="warning" key={warning}>
                {warning}
              </p>
            ))}
            <div className="send-options">
              <button
                className="primary"
                disabled={
                  busy ||
                  mail.sending ||
                  mail.working ||
                  ['accepted', 'uncertain'].includes(book.state)
                }
                onClick={() => void send(book)}
              >
                <Mail size={18} />
                {book.state === 'accepted'
                  ? '邮件已提交'
                  : book.state === 'sending'
                    ? '正在提交…'
                    : '邮箱直推到 Kindle'}
              </button>
              <button
                className="secondary"
                disabled={busy}
                onClick={() => void share(book)}
              >
                <Share2 size={18} />
                分享到 Kindle
              </button>
            </div>
            {activeMail.current === book.id && mail.message && (
              <p className="sync-status" role="status">
                {mail.message}
                {mail.sending && mail.progress > 0
                  ? ' · ' + mail.progress + '%'
                  : ''}
              </p>
            )}
            {book.mailJobId && (
              <button
                className="text-button"
                disabled={mail.sending || mail.working}
                onClick={() => void check(book)}
              >
                查询发送结果
              </button>
            )}
            {book.state === 'accepted' && (
              <p className="subtle">
                邮件已提交到发件邮箱，仍需 Amazon 处理，不代表 Kindle 已收到。
              </p>
            )}
            {book.state === 'uncertain' && (
              <p className="warning">
                发送结果待确认，请先查询结果，避免重复寄送。
              </p>
            )}
            <div className="inline-actions">
              <button
                className="text-button"
                disabled={busy}
                onClick={() => download(book)}
              >
                <Download size={16} />
                保存修复版
              </button>
              <button
                className="text-button"
                disabled={busy}
                onClick={() => setModal({ kind: 'preview', id: book.id })}
              >
                预览
              </button>
            </div>
            <details>
              <summary>分享菜单没有 Kindle？</summary>
              <p className="subtle">
                保存到「文件」，长按书籍分享给 Kindle App，或打开 Amazon
                网页上传修复版。
              </p>
              <a
                className="text-button"
                href={amazon}
                target="_blank"
                rel="noopener noreferrer"
              >
                Amazon 网页发送 <ArrowUpRight size={15} />
              </a>
            </details>
          </>
        )}
        {book.error && (
          <p className="error" role="alert">
            {book.error}
          </p>
        )}
        {!busy && unfinished(book) && (
          <button
            className="text-button muted"
            onClick={() => updateBook(book.id, { state: 'skipped' })}
          >
            暂时跳过
          </button>
        )}
      </div>
    );
  }
  function navigate(next: Page) {
    setPage(next);
    window.scrollTo({
      top: 0,
      behavior: appearance.reduced ? 'instant' : 'smooth',
    });
  }
  return (
    <div className="app">
      <header className="app-header">
        <a
          className="brand"
          href="#home"
          onClick={(e) => {
            e.preventDefault();
            navigate('home');
          }}
        >
          <img src="./icon.svg" alt="" />
          <span>
            轻阅<small>让好书，轻松抵达</small>
          </span>
        </a>
        <div className="header-actions">
          <button
            className="small-button"
            onClick={() => setModal({ kind: 'library' })}
          >
            <BookOpen size={18} />
            书库
          </button>
          <button
            className="icon-button"
            aria-label="设置"
            onClick={() => setModal({ kind: 'settings' })}
          >
            <Settings size={21} />
          </button>
        </div>
      </header>
      <main>
        {page === 'home' && (
          <div className="page" key="home">
            <section className="daily">
              <span className="eyebrow">每日一句</span>
              <blockquote>{quote[0]}</blockquote>
              <a href={quote[2]} target="_blank" rel="noopener noreferrer">
                — {quote[1]}
              </a>
            </section>
            <section className="search-card" aria-label="搜索书籍与漫画">
              <div className="section-top">
                <div className="segmented">
                  {(['book', 'comic'] as Kind[]).map((value) => (
                    <button
                      key={value}
                      aria-pressed={kind === value}
                      className={kind === value ? 'active' : ''}
                      onClick={() => setKind(value)}
                    >
                      {value === 'book' ? '书籍' : '漫画'}
                    </button>
                  ))}
                </div>
                <span className="subtle">
                  {kind === 'book' ? 'Z-Library' : 'koz.moe'}
                </span>
              </div>
              <form
                className="search-form"
                action={
                  kind === 'book' ? sites.book + 's/' : sites.comic + 'list.php'
                }
                method="get"
                target="_blank"
                onSubmit={search}
              >
                <Search size={20} aria-hidden="true" />
                <input
                  aria-label={kind === 'book' ? '搜索书籍' : '搜索漫画'}
                  name={kind === 'book' ? 'q' : 's'}
                  value={queries[kind]}
                  placeholder={
                    kind === 'book' ? '书名、作者或 ISBN' : '漫画名或作者'
                  }
                  maxLength={200}
                  onChange={(e) => setQuery(e.target.value)}
                  onCompositionStart={() => {
                    composing.current = true;
                  }}
                  onCompositionEnd={() => {
                    composing.current = false;
                  }}
                  onKeyDown={(e) => {
                    if (
                      e.key === 'Enter' &&
                      (e.nativeEvent.isComposing || composing.current)
                    )
                      e.preventDefault();
                  }}
                />
                <button
                  className="search-submit"
                  aria-label={
                    kind === 'book'
                      ? '搜索书籍 · 打开书库'
                      : '搜索漫画 · 打开书库'
                  }
                  type="submit"
                >
                  <ArrowUpRight size={21} />
                </button>
              </form>
              <div className="search-footer">
                <span>下载 EPUB 后回来导入</span>
                <button
                  className={'wish-button ' + (wished ? 'selected' : '')}
                  onClick={() => toggleWish(queries[kind], kind)}
                >
                  {wished ? <Check size={14} /> : <Plus size={14} />}
                  {wished ? '已加入想读' : '想读'}
                </button>
              </div>
              {!!recent.length && (
                <details className="recent-searches">
                  <summary>最近搜索</summary>
                  <div className="chips">
                    {recent.slice(0, 8).map((item, i) => (
                      <button
                        key={i}
                        onClick={() => {
                          setKind(item.kind);
                          const next = { ...queries, [item.kind]: item.query };
                          setQueries(next);
                          local('qingyue.search-queries', next);
                        }}
                      >
                        {item.query}
                        <small>{item.kind === 'comic' ? '漫画' : '书籍'}</small>
                      </button>
                    ))}
                    <button
                      onClick={() => {
                        setRecent([]);
                        local('qingyue.mobile-search-history', []);
                      }}
                    >
                      清空
                    </button>
                  </div>
                </details>
              )}
            </section>
            <section
              className="import-card"
              onDragOver={(e) => e.preventDefault()}
              onDrop={(e) => {
                e.preventDefault();
                admit(Array.from(e.dataTransfer.files));
              }}
            >
              <div className="import-symbol">
                <Upload size={26} />
              </div>
              <h1>选一本，开始读。</h1>
              <p>
                检查 EPUB，补齐图片登记
                <br />
                原书保留，修复版另存
              </p>
              <button
                className="primary full"
                disabled={loading}
                onClick={() => fileInput.current?.click()}
              >
                <Plus size={20} />
                选择书籍
              </button>
              <div className="import-foot">
                <span>可多选 · 本地修复 ≤ 50 MB</span>
                <button
                  className="text-button"
                  onClick={() => setModal({ kind: 'help' })}
                >
                  使用说明
                </button>
              </div>
              <button
                className="sample-button"
                disabled={loading}
                onClick={() =>
                  void sampleBook()
                    .then((file) => admit([file]))
                    .catch((e) => setNotice(fault(e)))
                }
              >
                第一次使用？试修一本示例 <ChevronRight size={14} />
              </button>
            </section>
            {active && (
              <section className="result-card">
                <div className="section-top">
                  <div>
                    <span className="eyebrow">本次导入</span>
                    <h2>{active.title}</h2>
                  </div>
                  <button
                    className="icon-button"
                    aria-label="书籍详情"
                    onClick={() => setModal({ kind: 'book', id: active.id })}
                  >
                    <MoreHorizontal size={22} />
                  </button>
                </div>
                <p className="subtle">
                  {labels[active.state]} · {fileSize(active.bytes)}
                  {!active.persisted && ' · 仅本次使用'}
                </p>
                {actions(active)}
              </section>
            )}
            {!!queue.length && (
              <button
                className="queue-shortcut"
                onClick={() => navigate('queue')}
              >
                <ListOrdered size={19} />
                <span>
                  发送队列
                  <strong>
                    {queue.length} 本待处理{paused ? ' · 已暂停' : ''}
                  </strong>
                </span>
                <ChevronRight size={18} />
              </button>
            )}
          </div>
        )}
        {page === 'shelf' && (
          <div className="page" key="shelf">
            <div className="page-title">
              <div>
                <span className="eyebrow">MY LIBRARY</span>
                <h1>我的书架</h1>
              </div>
              <span className="count">{books.length} 本</span>
            </div>
            <div className="shelf-tools">
              <label className="filter-search">
                <Search size={18} />
                <input
                  aria-label="搜索我的书架"
                  placeholder="搜索书名或作者"
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                />
              </label>
              <div className="section-top">
                <button
                  className={'filter-button ' + (favorites ? 'selected' : '')}
                  aria-pressed={favorites}
                  onClick={() => setFavorites(!favorites)}
                >
                  <Star size={15} />
                  只看收藏
                </button>
                <select
                  aria-label="筛选分类"
                  value={categoryFilter}
                  onChange={(e) => setCategoryFilter(e.target.value)}
                >
                  <option>全部</option>
                  {categories.map((c) => (
                    <option key={c}>{c}</option>
                  ))}
                </select>
              </div>
            </div>
            <p className="subtle storage-note">
              保存在此浏览器，清理网站数据会移除文件。重要书籍请另存。
            </p>
            {loading ? (
              <p role="status">正在读取本机书架…</p>
            ) : !filtered.length ? (
              <div className="empty">
                <Library size={36} />
                <h2>{books.length ? '没有匹配的书籍' : '书架还空着'}</h2>
                <p>导入一本 EPUB，修复后留在这里。</p>
                <button className="secondary" onClick={() => navigate('home')}>
                  去导入
                </button>
              </div>
            ) : (
              <div className="shelf-list">
                {filtered.map((book) => (
                  <article className="shelf-item" key={book.id}>
                    <button
                      className="book-open"
                      onClick={() => {
                        setActiveId(book.id);
                        navigate('home');
                      }}
                    >
                      <Cover book={book} />
                      <div>
                        <h2>{book.title}</h2>
                        <p>{book.author || '作者未注明'}</p>
                        <span>
                          {labels[book.state]} · {book.category}
                        </span>
                        {book.favorite && (
                          <Star
                            className="favorite-mark"
                            size={13}
                            fill="currentColor"
                          />
                        )}
                      </div>
                    </button>
                    <button
                      className="icon-button"
                      aria-label={book.title + ' 详情'}
                      onClick={() => setModal({ kind: 'book', id: book.id })}
                    >
                      <MoreHorizontal size={22} />
                    </button>
                  </article>
                ))}
              </div>
            )}
          </div>
        )}
        {page === 'queue' && (
          <div className="page" key="queue">
            <div className="page-title">
              <div>
                <span className="eyebrow">SEND QUEUE</span>
                <h1>发送队列</h1>
              </div>
              <span className="count">{queue.length} 本</span>
            </div>
            <div className="queue-controls">
              <span>
                {paused
                  ? '已暂停自动检查'
                  : running.current
                    ? '正在检查书籍'
                    : '等待处理'}
                <small>离开 Safari 时任务可能暂停</small>
              </span>
              <button className="secondary" onClick={() => pause(!paused)}>
                {paused ? '继续处理' : '暂停检查'}
              </button>
            </div>
            <p className="subtle">
              邮箱直推和分享由你逐本确认；暂停在当前书籍处理完后生效。
            </p>
            {!queue.length ? (
              <div className="empty">
                <Check size={36} />
                <h2>暂无待处理书籍</h2>
                <p>已提交的书籍可在书架中查看。</p>
              </div>
            ) : (
              queue.map((book) => (
                <article className="result-card" key={book.id}>
                  <div className="section-top">
                    <h2>{book.title}</h2>
                    <span className="status">{labels[book.state]}</span>
                  </div>
                  <p className="subtle">{fileSize(book.bytes)}</p>
                  {actions(book)}
                </article>
              ))
            )}
            <button
              className="secondary full"
              onClick={() => fileInput.current?.click()}
            >
              <Plus size={18} />
              继续添加书籍
            </button>
          </div>
        )}
        {page === 'wishes' && (
          <div className="page" key="wishes">
            <div className="page-title">
              <div>
                <span className="eyebrow">READ NEXT</span>
                <h1>想读清单</h1>
              </div>
              <span className="count">{wishes.length} 本</span>
            </div>
            <form
              className="wish-form"
              onSubmit={(e) => {
                e.preventDefault();
                if (!wishDraft.title.trim()) {
                  setNotice('请先填写书名。');
                  return;
                }
                if (
                  wishes.some(
                    (w) =>
                      w.title === wishDraft.title.trim() &&
                      w.kind === wishDraft.kind,
                  )
                ) {
                  setNotice('这本书已在想读清单中。');
                  return;
                }
                toggleWish(wishDraft.title, wishDraft.kind, wishDraft.author);
                setWishDraft({ ...wishDraft, title: '', author: '' });
              }}
            >
              <label className="field-label">
                书名
                <input
                  aria-label="想读书名"
                  maxLength={200}
                  value={wishDraft.title}
                  onChange={(e) =>
                    setWishDraft({ ...wishDraft, title: e.target.value })
                  }
                  placeholder="记下下一本想读的书"
                />
              </label>
              <div className="wish-fields">
                <label className="field-label">
                  作者（选填）
                  <input
                    maxLength={100}
                    value={wishDraft.author}
                    onChange={(e) =>
                      setWishDraft({ ...wishDraft, author: e.target.value })
                    }
                  />
                </label>
                <label className="field-label">
                  类型
                  <select
                    value={wishDraft.kind}
                    onChange={(e) =>
                      setWishDraft({
                        ...wishDraft,
                        kind: e.target.value as Kind,
                      })
                    }
                  >
                    <option value="book">书籍</option>
                    <option value="comic">漫画</option>
                  </select>
                </label>
              </div>
              <button className="secondary full" type="submit">
                <Plus size={18} />
                加入想读
              </button>
            </form>
            {!wishes.length ? (
              <div className="empty">
                <Heart size={34} />
                <h2>下一本，留在这里</h2>
                <p>书籍和漫画都可以加入想读。</p>
              </div>
            ) : (
              wishes.map((wish) => (
                <article className="wish-item" key={wish.id}>
                  <div>
                    <span className="eyebrow">
                      {wish.kind === 'comic' ? '漫画' : '书籍'}
                    </span>
                    <h2>{wish.title}</h2>
                    {wish.author && <p className="subtle">{wish.author}</p>}
                  </div>
                  <div className="inline-actions">
                    <a
                      className="small-button"
                      href={searchUrl(wish.kind, wish.title)}
                      target="_blank"
                      rel="noopener noreferrer"
                      onClick={() => rememberSearch(wish.kind, wish.title)}
                    >
                      去书库搜索 <ArrowUpRight size={16} />
                    </a>
                    <button
                      className="icon-button"
                      aria-label={'移除想读 ' + wish.title}
                      onClick={() => toggleWish(wish.title, wish.kind)}
                    >
                      <X size={18} />
                    </button>
                  </div>
                </article>
              ))
            )}
            <p className="subtle storage-note">
              想读清单保存在当前手机，不参与设备同步。
            </p>
          </div>
        )}
      </main>
      <input
        className="file-input"
        type="file"
        aria-label="选择 EPUB 书籍"
        accept=".epub,application/epub+zip"
        multiple
        ref={fileInput}
        onChange={(e) => {
          admit(Array.from(e.target.files || []));
          e.target.value = '';
        }}
      />
      {notice && (
        <div className="notice" role="status">
          <span>{notice}</span>
          <button aria-label="关闭提示" onClick={() => setNotice('')}>
            <X size={17} />
          </button>
        </div>
      )}
      <nav className="bottom-nav" aria-label="主导航">
        {nav.map((item) => (
          <button
            key={item.id}
            className={page === item.id ? 'active' : ''}
            aria-current={page === item.id ? 'page' : undefined}
            onClick={() => navigate(item.id)}
          >
            <item.icon size={22} />
            <span>{item.text}</span>
            {item.id === 'queue' && queue.length > 0 && <i aria-hidden="true">{queue.length}</i>}
          </button>
        ))}
      </nav>
      {modal && (
        <Sheet
          key={modal.kind}
          title={
            {
              settings: '轻阅设置',
              library: '书库与发送',
              help: '从选书到开读',
              clear: '清空本机书架？',
              book: '书籍详情',
              preview: modalBook?.title || '预览',
              remove: '移除这本书？',
            }[modal.kind]
          }
          close={() => setModal(null)}
        >
          {modal.kind === 'settings' && (
            <>
              <section className="settings-card">
                <h3>外观</h3>
                <label className="field-label">
                  主题
                  <select
                    value={appearance.theme}
                    onChange={(e) =>
                      updateAppearance({
                        theme: e.target.value as Appearance['theme'],
                      })
                    }
                  >
                    <option value="system">跟随系统</option>
                    <option value="light">浅色</option>
                    <option value="dark">深色</option>
                  </select>
                </label>
                <label className="setting-row">
                  <span>
                    <strong>更大的文字</strong>
                    <small>提高手机阅读舒适度</small>
                  </span>
                  <input
                    type="checkbox"
                    checked={appearance.large}
                    onChange={(e) =>
                      updateAppearance({ large: e.target.checked })
                    }
                  />
                </label>
                <label className="setting-row">
                  <span>
                    <strong>减少动效</strong>
                    <small>保留反馈，减少移动与缩放</small>
                  </span>
                  <input
                    type="checkbox"
                    checked={appearance.reduced}
                    onChange={(e) =>
                      updateAppearance({ reduced: e.target.checked })
                    }
                  />
                </label>
              </section>
              <SettingsPanel
                shared={shared}
                sync={sync}
                mail={mail}
                update={updateSettings}
                notice={setNotice}
                onClose={() => setModal(null)}
                onClear={() => setModal({ kind: 'clear' })}
              />
            </>
          )}
          {modal.kind === 'library' && (
            <>
              <p className="subtle">
                在 Safari 登录书库，下载后回到轻阅导入。登录状态由网站保存。
              </p>
              {(['book', 'comic'] as Kind[]).map((value) => (
                <a
                  key={value}
                  className="library-link"
                  href={sites[value]}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  <BookOpen size={23} />
                  <span>
                    <strong>
                      {value === 'book' ? '登录 Z-Library' : '登录 koz.moe'}
                    </strong>
                    <small>
                      {value === 'book'
                        ? '书籍搜索与下载'
                        : '漫画搜索 · 网站支持 Kindle 推送'}
                    </small>
                  </span>
                  <ArrowUpRight size={18} />
                </a>
              ))}
              <a
                className="library-link"
                href={amazon}
                target="_blank"
                rel="noopener noreferrer"
              >
                <Upload size={23} />
                <span>
                  <strong>Amazon 网页发送</strong>
                  <small>大文件上传 · 需在网页确认</small>
                </span>
                <ArrowUpRight size={18} />
              </a>
              <p className="subtle">
                手机版无法自动接收外站下载，也不能后台操作外站账号。邮件直推无需电脑开机，首次使用请在设置连接发件邮箱。
              </p>
            </>
          )}
          {modal.kind === 'help' && (
            <>
              <div className="help-step">
                <span>01</span>
                <div>
                  <h3>选入 EPUB</h3>
                  <p>
                    书库下载后，保存到
                    iPhone「文件」。回到轻阅选择一本或多本书。
                  </p>
                </div>
              </div>
              <div className="help-step">
                <span>02</span>
                <div>
                  <h3>检查与修复</h3>
                  <p>
                    补齐图片登记，重新整理 EPUB
                    打包，不改变正文、目录和插画。缺少原图与兼容问题会单独提示。
                  </p>
                </div>
              </div>
              <div className="help-step">
                <span>03</span>
                <div>
                  <h3>自由选择发送方式</h3>
                  <p>
                    邮箱直推，或在 iPhone 分享菜单选 Kindle App；网页发送需在
                    Amazon 确认。
                  </p>
                </div>
              </div>
              <p className="warning">
                手机本地修复支持 50 MB 以内的 EPUB；50–200 MB
                文件请先用电脑版修复，再通过 Amazon 网页发送。
              </p>
              <p className="subtle">
                修复在本机完成。邮箱发送会临时上传修复版用于寄出。书架存于当前浏览器，重要文件请另存。Safari
                分享 → 添加到主屏幕，可像 App 一样打开。
              </p>
              <button className="primary full" onClick={() => setModal(null)}>
                知道了
              </button>
            </>
          )}
          {modal.kind === 'book' && modalBook && (
            <BookDetails
              key={modalBook.id}
              book={modalBook}
              update={(patch) => updateBook(modalBook.id, patch)}
              remove={() => setModal({ kind: 'remove', id: modalBook.id })}
              preview={() => setModal({ kind: 'preview', id: modalBook.id })}
            />
          )}
          {modal.kind === 'preview' && modalBook && (
            <Preview book={modalBook} />
          )}
          {modal.kind === 'remove' && modalBook && (
            <>
              <p>
                移除「{modalBook.title}」及此浏览器保存的文件？已下载的副本和
                Kindle 中的书不会删除。
              </p>
              <button
                className="primary full"
                disabled={['repairing', 'sending'].includes(modalBook.state)}
                onClick={() => void remove(modalBook.id)}
              >
                确认移除
              </button>
              <button
                className="secondary full"
                onClick={() => setModal({ kind: 'book', id: modalBook.id })}
              >
                取消
              </button>
            </>
          )}
          {modal.kind === 'clear' && (
            <>
              <p>
                清空此浏览器的书架与保存文件，邮箱设置和想读清单将保留。此操作无法撤销。
              </p>
              <button
                className="primary full"
                disabled={running.current || mail.sending}
                onClick={() => void clear()}
              >
                确认清空
              </button>
              <button
                className="secondary full"
                onClick={() => setModal({ kind: 'settings' })}
              >
                取消
              </button>
            </>
          )}
        </Sheet>
      )}
    </div>
  );
}
