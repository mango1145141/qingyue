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
          <p className="sync-note">
            首次授权尚未完成，请先在安全授权页提交邮箱授权码，然后连接邮箱。
          </p>
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
