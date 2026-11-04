import type { BrowserAuth } from "./auth.js";
import { $, returnFocus } from "./live-dom.js";
import { asError } from "./transport.js";

export interface SettingsDialogDeps {
  panel: HTMLElement;
  dialog: HTMLDialogElement;
  micDevice: HTMLSelectElement;
  auth: Pick<BrowserAuth, "isDemo" | "isFake" | "signedIn" | "accountName" | "signIn">;
  /** The microphone the settings keep. */
  deviceId(): string;
  /** The panel is in the floating window; the dialog opens on the page, which is brought forward first. */
  floating(): boolean;
  /** The dialog closed: its fields are saved and applied. */
  closed(): void;
}
export interface SettingsDialog {
  /** Opens the dialog, with focus on `field` when one is given (the "내 메모" chip opens the notes). */
  open(field?: HTMLElement): void;
  /** Lists the microphones, keeping the chosen one when it is still there. */
  fillDevices(devices: MediaDeviceInfo[]): void;
}

/** The settings dialog: the audio source and microphone, the notes, and the account. */
export function createSettingsDialog({ panel, dialog, micDevice, auth, deviceId, floating, closed }: SettingsDialogDeps): SettingsDialog {
  function fillDevices(devices: MediaDeviceInfo[]): void {
    const selected = deviceId();
    micDevice.replaceChildren(new Option("브라우저 기본 마이크", ""));
    for (const device of devices.filter(value => value.kind === "audioinput"))
      micDevice.add(new Option(device.label || "마이크", device.deviceId));
    micDevice.value = [...micDevice.options].some(option => option.value === selected) ? selected : "";
  }
  function renderAccount(): void {
    const label = auth.isDemo ? "로그인" : "Microsoft 계정 연결";
    $("account-status").textContent = auth.isFake ? "로컬 테스트 서버 (로그인 불필요)"
      : auth.signedIn ? `로그인됨: ${auth.accountName} · 이 브라우저에서 로그인이 유지됩니다`
      : auth.isDemo ? "아직 로그인하지 않았습니다." : "Microsoft 계정이 아직 연결되지 않았습니다.";
    $("signin").hidden = auth.isFake;
    $("signin").textContent = auth.signedIn ? (auth.isDemo ? "다시 로그인" : "다른 계정으로 연결") : label;
  }
  /** The element the dialog was opened from; focus returns to it when the dialog closes. */
  let opener: Element | null = null;
  function open(field?: HTMLElement): void {
    opener = panel.ownerDocument.activeElement;
    if (floating()) window.focus();
    renderAccount();
    void navigator.mediaDevices?.enumerateDevices().then(fillDevices).catch(() => undefined);
    dialog.showModal();
    field?.focus();
  }
  $("signin").addEventListener("click", () => {
    void auth.signIn().then(renderAccount, error => { $("account-status").textContent = `로그인 실패: ${asError(error).message}`; });
  });
  dialog.addEventListener("close", () => {
    closed();
    // The browser has already put focus back where the dialog was opened from. The close event comes a task later, when focus
    // may have moved on: only focus left nowhere or in the closed dialog (its opener is gone or turned off) goes to the opener
    // or the panel.
    const focused = panel.ownerDocument.activeElement;
    if (!focused || focused === panel.ownerDocument.body || dialog.contains(focused)) returnFocus(panel, opener);
  });
  return { open, fillDevices };
}
