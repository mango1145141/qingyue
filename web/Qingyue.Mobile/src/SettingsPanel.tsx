import { useRef, useState } from 'react';
import { Smartphone, Check } from 'lucide-react';
import { useSettingsSync, type SharedSettings } from './settingsSync';
import { useCloudMail } from './cloudMail';
type Props = {
  shared: SharedSettings;
  sync: ReturnType<typeof useSettingsSync>;
  mail: ReturnType<typeof useCloudMail>;
  update: (patch: Partial<SharedSettings>) => void;
  notice: (text: string) => void;
  onClose: () => void;
  onClear: () => void;
};
export default function SettingsPanel({
  shared,
  sync,
  mail,
  update,
  notice: setNotice,
  onClose,
  onClear,
}: Props) {
  const [emailDraft, setEmailDraft] = useState({
    senderEmail: shared.senderEmail,
    kindleEmail: shared.kindleEmail,
  });
  const emailDirty = useRef(false);
  const [emailMessage, setEmailMessage] = useState('');
  const [pairInput, setPairInput] = useState('');
  function saveEmails() {
    const senderEmail = emailDraft.senderEmail.trim();
    const kindleEmail = emailDraft.kindleEmail.trim();
    if (
      !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(senderEmail) ||
      !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(kindleEmail)
    ) {
      setEmailMessage('请输入有效的发件邮箱和 Kindle 接收邮箱。');
      return false;
    }
    update({ senderEmail, kindleEmail });
    emailDirty.current = false;
    setEmailMessage('邮箱已保存。首次发送请连接发件邮箱。');
    return true;
  }
  // Synced settings can arrive while the sheet is open; preserve an unfinished edit.
  const [receivedEmails, setReceivedEmails] = useState(
    shared.senderEmail + shared.kindleEmail,
  );
  const incoming = shared.senderEmail + shared.kindleEmail;
  if (incoming !== receivedEmails) {
    setReceivedEmails(incoming);
    if (!emailDirty.current)
      setEmailDraft({
        senderEmail: shared.senderEmail,
        kindleEmail: shared.kindleEmail,
      });
  }
  return (
    <div className="settings-content">
      <p className="modal-intro">配对一次，让手机和电脑记住同一份设置。</p>
      <section className="settings-card">
        <h3>邮箱</h3>
        <label className="field-label">
          发件邮箱
          <input
            type="email"
            maxLength={254}
            autoCapitalize="none"
            autoCorrect="off"
            value={emailDraft.senderEmail}
            onChange={(event) => {
              emailDirty.current = true;
              setEmailDraft({ ...emailDraft, senderEmail: event.target.value });
            }}
          />
        </label>
        <label className="field-label">
          Kindle 接收邮箱
          <input
            type="email"
            maxLength={254}
            autoCapitalize="none"
            autoCorrect="off"
            value={emailDraft.kindleEmail}
            onChange={(event) => {
              emailDirty.current = true;
              setEmailDraft({ ...emailDraft, kindleEmail: event.target.value });
            }}
          />
        </label>
        <button className="secondary full" onClick={saveEmails}>
          保存邮箱
        </button>
        <p className="sync-note" role="status">
          {emailMessage || '用于手机邮箱直推和电脑版发送。'}
        </p>
      </section>
      <section className="settings-card">
        <h3>
          手机邮箱直推{' '}
          <span>{mail.connection?.connected ? '已连接' : '未连接'}</span>
        </h3>
        <p className="sync-note">
          首次连接后，手机能直接寄出修复版，电脑无需开机。
          {mail.connection?.provider &&
            '已识别 ' + mail.connection.provider + '。'}
        </p>
        {!sync.connected && (
          <>
            <p className="sync-note">
              先连接已配对的设备，或开启设备同步，以保存邮箱连接。
            </p>
            <button
              className="secondary full"
              disabled={sync.working}
              onClick={() => void sync.pair()}
            >
              开启设备同步
            </button>
          </>
        )}
        {sync.connected && mail.connection && !mail.connection.configured && (
          <div className="mail-authorization">
            <p className="sync-note">
              邮箱地址已同步，发信授权还需连接一次。按下面的步骤获取授权码，无需填写服务器参数。
            </p>
            {mail.connection.authorizationUrl &&
            (mail.connection.authorizationExpiresAt || 0) > Date.now() ? (
              <a
                className="secondary full"
                href={mail.connection.authorizationUrl}
                target="_blank"
                rel="noreferrer"
              >
                打开安全授权页
              </a>
            ) : (
              <>
                <p className="sync-note">
                  当前没有可用的安全授权链接。点击下方申请入口，附带当前邮箱的授权配置编号，由维护者生成专用安全页。请勿在留言或聊天中发送授权码。
                </p>
                <a
                  className="secondary full"
                  href={
                    'https://github.com/mango1145141/qingyue/issues/new?title=' +
                    encodeURIComponent('申请手机邮箱安全授权入口') +
                    '&body=' +
                    encodeURIComponent(
                      '授权配置编号：' +
                        (mail.connection.authorizationId ||
                          '请先立即同步并重新打开设置') +
                        '\n请为此配对生成安全授权页。此处不包含邮箱密码或授权码。',
                    )
                  }
                  target="_blank"
                  rel="noreferrer"
                >
                  申请安全授权入口
                </a>
              </>
            )}
            <p className="sync-note">
              安全页提交后，等待授权绑定，再点下方「连接发件邮箱」。修改发件邮箱后需重新授权。
            </p>
          </div>
        )}
        <button
          className="primary full"
          disabled={!sync.connected || mail.working || mail.sending}
          onClick={() => {
            if (emailDirty.current && !saveEmails()) return;
            void (async () => {
              await sync.flush();
              await mail.connect();
            })();
          }}
        >
          {mail.working
            ? '正在连接…'
            : mail.connection?.connected
              ? '重新连接发件邮箱'
              : '连接发件邮箱'}
        </button>
        {mail.connection?.configured && !mail.connection.connected && (
          <p className="sync-note">发信授权已绑定，点击连接即可使用。</p>
        )}
        <details className="mail-help">
          <summary>如何获取邮箱授权码？</summary>
          <p className="sync-note">
            163 手机渠道：网易邮箱大师 → 我 → 邮箱管理 → 选择发件邮箱 →
            第三方登录管理 → 通用授权码 → 新增授权码。
          </p>
          <p className="sync-note">
            163 网页渠道：登录网易邮箱，打开设置 → POP3/SMTP/IMAP →
            新增授权密码。授权码仅在生成时显示一次，用于允许轻阅发信。
          </p>
          <a
            className="text-button"
            href="https://email.163.com/"
            target="_blank"
            rel="noreferrer"
          >
            打开网易邮箱官网 ↗
          </a>
          <p className="sync-note">
            QQ / Foxmail 在邮箱设置中开启 IMAP/SMTP 并生成授权码；Gmail
            使用应用专用密码。仅在轻阅的专用安全页提交，不能把邮箱登录密码填进去。
          </p>
        </details>
        {mail.connection?.connected && (
          <>
            <p className="sync-note">
              此邮箱直推支持{' '}
              {Math.floor(mail.connection.maxFileBytes / 1024 / 1024)} MB
              以内的修复版。
            </p>
            <button
              className="text-button"
              disabled={mail.working || mail.sending}
              onClick={() => void mail.disconnect()}
            >
              断开手机邮件发送
            </button>
          </>
        )}
        {mail.message && (
          <p className="sync-status" role="status">
            {mail.message}
          </p>
        )}
      </section>
      <label className="setting-row">
        <span>
          <strong>选入后自动修复</strong>
          <small>无需再点一次检查按钮</small>
        </span>
        <input
          type="checkbox"
          checked={shared.autoRepair}
          onChange={(event) => update({ autoRepair: event.target.checked })}
        />
      </label>
      <label className="setting-row">
        <span>
          <strong>在本机保存书架</strong>
          <small>原书和修复版保存在此浏览器，设备同步不包含文件</small>
        </span>
        <input
          type="checkbox"
          checked={shared.rememberHistory}
          onChange={(event) =>
            update({ rememberHistory: event.target.checked })
          }
        />
      </label>
      <label className="setting-row">
        <span>
          <strong>电脑修复后自动发送</strong>
          <small>手机修复后可选邮箱直推或分享到 Kindle</small>
        </span>
        <input
          type="checkbox"
          checked={shared.autoSend}
          onChange={(event) => {
            update({ autoSend: event.target.checked });
          }}
        />
      </label>
      <section className="settings-card sync-card">
        <h3>
          设备同步 <span>{sync.connected ? '已连接' : '未配对'}</span>
        </h3>
        <p className="sync-note">
          在电脑版右上角点「设备同步」，用 iPhone
          相机扫描二维码即可连接。同步邮箱和偏好。书籍不会跟随设备同步，手机发信授权单独连接，书库登录保留在各自设备上。
        </p>
        <p className="sync-status" role="status">
          {sync.status || '首次连接后，打开页面时自动同步。'}
        </p>
        {sync.connected && (
          <p className="sync-note">已连接 {sync.state?.devices || 1} 台设备</p>
        )}
        <button
          className="secondary full"
          disabled={sync.working}
          onClick={() => {
            if (emailDirty.current && !saveEmails()) return;
            void sync.pair();
          }}
        >
          生成配对链接
        </button>
        {sync.pairLink && (
          <>
            <textarea
              className="pair-link"
              aria-label="配对链接"
              readOnly
              value={sync.pairLink}
            />
            <p className="sync-note">链接 5 分钟内有效，请仅交给自己的设备。</p>
            <button
              className="secondary full"
              onClick={() => {
                if (navigator.clipboard)
                  void navigator.clipboard
                    .writeText(sync.pairLink)
                    .then(() => setNotice('配对链接已复制。'))
                    .catch(() => setNotice('请长按配对链接手动复制。'));
              }}
            >
              复制配对链接
            </button>
          </>
        )}
        <label className="field-label">
          或粘贴另一台设备的配对链接
          <input
            value={pairInput}
            onChange={(event) => setPairInput(event.target.value)}
            autoCapitalize="none"
            autoCorrect="off"
          />
        </label>
        <button
          className="secondary full"
          disabled={sync.working || !pairInput.trim()}
          onClick={() => void sync.claim(pairInput)}
        >
          连接设备
        </button>
        {sync.connected && (
          <div className="sync-actions">
            <button
              className="text-button"
              disabled={sync.working}
              onClick={() => void sync.retry()}
            >
              立即同步
            </button>
            <button
              className="text-button"
              disabled={sync.working}
              onClick={() => void sync.disconnect()}
            >
              断开此设备
            </button>
          </div>
        )}
      </section>
      <div className="settings-summary">
        <Smartphone size={20} />
        <div>
          <strong>两种方式，自由选择</strong>
          <p>邮箱直推到 Kindle，或在 iPhone 分享菜单选择 Kindle App。</p>
        </div>
      </div>
      <p className="saved">
        <Check size={14} />
        设置更改会自动保存
      </p>
      <button className="secondary full" onClick={onClear}>
        清空本机书架
      </button>
      <button
        className="primary full"
        onClick={() => {
          if (emailDirty.current && !saveEmails()) return;
          setNotice('设置已保存。');
          onClose();
        }}
      >
        完成
      </button>
    </div>
  );
}
