"use strict";

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const icon = (name, className = "") => `<svg class="${className}" aria-hidden="true"><use href="#i-${name}"/></svg>`;
const escapeHTML = (value) => String(value).replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]);

const devices = {
  pixel: { id: "pixel", name: "Pixel 8 Pro", serial: "38271FDJH00C8T", connection: "USB", state: "online", android: "Android 15", model: "Google · husky", avatar: "" },
  xiaomi: { id: "xiaomi", name: "Xiaomi 14", serial: "192.168.1.106:5555", connection: "网络", state: "online", android: "Android 14", model: "Xiaomi · houji", avatar: "mint" },
  samsung: { id: "samsung", name: "Galaxy S23", serial: "R5CW30A7X2P", connection: "USB", state: "unauthorized", android: "未获取", model: "Samsung", avatar: "sand" },
  oneplus: { id: "oneplus", name: "OnePlus 12", serial: "53eac2d1", connection: "USB", state: "offline", android: "未获取", model: "OnePlus", avatar: "" },
};
const deviceStates = {
  online: { label: "在线", tone: "green", marker: "✓" },
  unauthorized: { label: "未授权", tone: "amber", marker: "!" },
  offline: { label: "离线", tone: "gray", marker: "−" },
};
const scenarios = { healthy: ["pixel", "xiaomi"], mixed: ["pixel", "xiaomi", "samsung"], offline: ["pixel", "oneplus"], empty: [], error: [] };
const state = { scenario: "mixed", selected: null, pinned: false, collapsed: false, side: null, edgeOffset: null, topmost: true, task: null, generation: 0 };
const desktop = $("#desktop");
const dock = $("#dock");
const edgeTab = $("#edge-tab");
const dialog = $("#action-dialog");
let toastTimer;
let drag = null;
let edgePress = null;
let longPressTimer;
let suppressEdgeClick = false;
let filePickerOpen = false;
let fileTarget = null;
let dialogReturnFocus = null;
let initialized = false;

function currentDevices() {
  return scenarios[state.scenario].map((id) => devices[id]);
}

function currentDevice() {
  return currentDevices().find((device) => device.id === state.selected);
}

function notify(message) {
  const toast = $("#toast");
  clearTimeout(toastTimer);
  toast.textContent = message;
  toast.hidden = false;
  toastTimer = setTimeout(() => { toast.hidden = true; }, 3300);
}

function footer(message, tone = "green", busy = false) {
  const element = $("#footer-message");
  element.innerHTML = `<span class="${busy ? "busy-indicator" : `status-dot ${tone}`}"></span><span>${escapeHTML(message)}</span>`;
  element.title = message;
  $("#footer-label").textContent = busy ? "模拟执行中" : "模拟环境";
}

function rowMarkup(device, index) {
  const online = device.state === "online";
  const status = deviceStates[device.state];
  return `<div class="device-row ${index === 0 ? "primary" : ""}" role="button" tabindex="0" data-device="${device.id}" aria-label="${escapeHTML(device.name)}，${status.label}，${online ? "查看操作" : "查看连接指引"}">
    <span class="device-avatar ${device.avatar}">${icon("device")}</span>
    <div class="device-identity"><div class="device-name-line"><span class="device-name">${device.name}</span><span class="connection-badge">${device.connection === "网络" ? icon("wifi") : ""}${device.connection}</span></div><div class="device-serial">${device.serial}</div></div>
    <div class="device-end"><span class="device-state ${online ? "" : device.state === "offline" ? "offline" : "warning"}"><span class="status-dot ${status.tone}"></span>${status.label}</span><span class="row-action">${online ? `<button class="quick-camera" data-quick="${device.id}" aria-label="对 ${device.name} 模拟截图" title="快捷截图">${icon("camera")}</button>` : "查看指引"}${icon("chevron")}</span></div>
  </div>`;
}

function listMarkup() {
  const list = currentDevices();
  const online = list.filter((device) => device.state === "online").length;
  const unauthorized = list.filter((device) => device.state === "unauthorized").length;
  const offline = list.filter((device) => device.state === "offline").length;
  const unavailable = list.length - online;
  const unavailableText = [unauthorized ? `${unauthorized} 台待授权` : "", offline ? `${offline} 台离线` : ""].filter(Boolean).join(" · ");
  const error = state.scenario === "error";
  const overview = `<div class="list-overview"><div><div class="overview-title"><h2>我的设备</h2><span class="device-quantity">${error ? "—" : list.length}</span></div><div class="overview-status"><span class="status-dot ${error ? "red" : online ? "green" : "gray"}"></span>${error ? "ADB 服务不可用" : list.length ? `<span class="online-text">${online} 台在线</span><span class="status-separator">/</span><span class="${unavailable ? "warning-text" : ""}">${unavailable ? unavailableText : "全部准备就绪"}</span>` : "等待设备连接"}</div></div><button class="refresh-button" id="refresh-devices" aria-label="刷新模拟设备状态" title="刷新设备">${icon("refresh")}</button></div>`;
  if (!list.length) {
    return `${overview}<div class="empty-state ${error ? "error-state" : ""}"><div class="empty-illustration">${icon(error ? "command" : "usb")}</div><h3>${error ? "ADB 暂时不可用" : "等待一位新朋友"}</h3><p>${error ? "无法连接本地 ADB 服务。<br>检查程序路径，让连接恢复正常。" : "连接 Android 设备并开启 USB 调试，<br>就会在这里看到它。"}</p><button class="primary-button" id="empty-action">${icon(error ? "settings" : "info")}${error ? "检查 ADB 设置" : "看看如何连接"}</button><span class="empty-hint">${error ? "服务异常不等于没有设备" : "支持 USB、已连接的无线设备和模拟器"}</span></div>`;
  }
  return `${overview}<div class="list-section-caption"><span>已发现的设备</span><small>点击设备，开始操作</small></div><div class="device-list">${list.map(rowMarkup).join("")}</div>${unauthorized ? `<div class="auth-notice">${icon("info")}<p>有设备正在等待授权，请在手机上允许 USB 调试。<br><button id="authorization-help">查看连接指引</button></p></div>` : `<div class="operation-note">${icon(offline ? "info" : "check")}<span>${offline ? "有设备暂时无法通信，请检查连接线或重新连接。" : "所有设备已就绪。选择一台设备，即可使用快捷操作。"}</span></div>`}`;
}

function detailMarkup(device) {
  const actions = [
    ["mirror", "mirror", "设备投屏", "primary-action"],
    ["screenshot", "camera", "截图保存", ""],
    ["install", "install", "安装 APK", ""],
    ["keys", "grid", "按键控制", ""],
    ["info", "info", "设备信息", ""],
    ["logcat", "log", "查看日志", ""],
  ];
  return `<div class="detail-heading"><button class="icon-button" id="back-button" aria-label="返回设备列表">${icon("back")}</button><h2>设备操作</h2><span>已绑定当前设备</span></div>
    <div class="device-detail-card"><span class="device-avatar ${device.avatar}">${icon("device")}</span><div><div class="detail-device-name">${device.name}</div><div class="detail-meta"><span class="status-dot green"></span><span>在线</span><span>·</span><span>${device.android}</span><span>·</span><span>${device.connection}</span></div><div class="detail-serial"><span>${device.serial}</span><button class="icon-button" id="copy-serial" aria-label="复制设备序列号" title="复制序列号">${icon("copy")}</button></div></div></div>
    <p class="action-section-label">常用操作</p><div class="action-grid">${actions.map(([action, image, label, className]) => `<button class="device-action ${className}" data-action="${action}" ${state.task?.deviceId === device.id ? "disabled" : ""}>${icon(image)}<span>${label}</span></button>`).join("")}</div>
    <div class="operation-note">${icon("info")}<span>仅操作当前设备。投屏由 scrcpy 打开独立窗口；原型不会执行真实命令。</span></div>`;
}

function render() {
  const device = currentDevice();
  $("#dock-content").innerHTML = device ? detailMarkup(device) : listMarkup();
  $("#view-caption").textContent = state.collapsed ? "贴边状态" : device ? "设备操作" : "设备面板";
  $$(".scenario").forEach((button) => {
    const selected = button.dataset.scenario === state.scenario;
    button.classList.toggle("selected", selected);
    button.setAttribute("aria-pressed", selected);
  });
  renderEdge();
  bindContent();
}

function renderEdge() {
  const list = currentDevices();
  const online = list.filter((device) => device.state === "online").length;
  const error = state.scenario === "error";
  const warning = online !== list.length;
  const hasOffline = list.some((device) => device.state === "offline");
  const tone = error ? "error" : !list.length ? "empty" : warning ? "warning" : "good";
  const horizontal = state.side === "top" || state.side === "bottom";
  const visibleCount = Math.max(1, Math.min(5, Math.floor((horizontal ? desktop.clientWidth - 158 : desktop.clientHeight - 180) / 39)));
  edgeTab.className = `edge-tab tone-${tone}${state.side ? ` side-${state.side}` : ""}${horizontal ? " horizontal" : ""}${edgePress?.active ? " is-dragging" : ""}`;
  $("#edge-status").textContent = error ? "异常" : !list.length ? "未连接" : hasOffline ? "有离线" : warning ? "待授权" : "在线";
  $("#edge-count").innerHTML = error ? "—" : `${online}<span>/${list.length}</span>`;
  $(".edge-state .status-dot").className = `status-dot ${error ? "red" : !list.length ? "gray" : warning ? "amber" : "green"}`;
  const description = error ? "ADB 异常，设备数量未知" : `${online} 台在线，共发现 ${list.length} 台${warning ? "，有设备需要处理" : ""}`;
  edgeTab.setAttribute("aria-label", `${description}；每个图标对应一台设备，长按ADB 和数量区域可拖到四边`);
  $("#edge-expand-button").setAttribute("aria-label", `${description}，短按展开、长按拖动，方向键切换停靠边`);
  $("#edge-device-list").innerHTML = error || !list.length
    ? `<button class="edge-placeholder ${error ? "error" : ""}" data-edge-placeholder="${error ? "error" : "empty"}" aria-label="${error ? "ADB 异常，检查设置" : "未发现设备，查看连接指引"}">${icon(error ? "warning" : "usb")}</button>`
    : list.slice(0, visibleCount).map((device) => {
      const status = deviceStates[device.state];
      const label = `${device.name}，${status.label}，${device.connection}，${device.serial}`;
      return `<button class="edge-device-button ${device.state}" data-edge-device="${escapeHTML(device.id)}" aria-label="${escapeHTML(label)}"><svg aria-hidden="true"><use href="#i-device"/></svg><span class="edge-device-marker" aria-hidden="true">${status.marker}</span><span class="edge-tooltip" aria-hidden="true"><b>${escapeHTML(device.name)}</b><em>${status.label} · ${device.connection}</em><small>${escapeHTML(device.serial)}</small></span></button>`;
    }).join("") + (list.length > visibleCount ? `<button class="edge-more" data-edge-more aria-label="另有 ${list.length - visibleCount} 台设备，展开全部设备">+${list.length - visibleCount}</button>` : "");
  if (state.collapsed && !edgePress?.active) positionEdge();
}

function positionEdge(offset = state.edgeOffset) {
  const horizontal = state.side === "top" || state.side === "bottom";
  const workHeight = desktop.clientHeight - 48;
  const maxOffset = Math.max(8, horizontal ? desktop.clientWidth - edgeTab.offsetWidth - 8 : workHeight - edgeTab.offsetHeight - 8);
  const preferred = offset ?? (horizontal ? (desktop.clientWidth - edgeTab.offsetWidth) / 2 : 180);
  state.edgeOffset = Math.max(8, Math.min(maxOffset, preferred));
  edgeTab.style.left = horizontal ? `${state.edgeOffset}px` : state.side === "left" ? "0px" : "auto";
  edgeTab.style.right = horizontal || state.side === "left" ? "auto" : "0px";
  edgeTab.style.top = `${horizontal ? state.side === "top" ? 0 : Math.max(0, workHeight - edgeTab.offsetHeight) : state.edgeOffset}px`;
}

function bindContent() {
  $("#refresh-devices")?.addEventListener("click", () => {
    if (state.scenario === "error") {
      footer("模拟重试失败，请检查 ADB 路径", "red");
      notify("当前是 ADB 异常场景，可在右侧切换为正常场景。");
    } else {
      footer("模拟设备状态已刷新");
      notify("设备状态已刷新（模拟数据）");
    }
  });
  $$("[data-device]").forEach((row) => {
    const open = () => openDevice(row.dataset.device);
    row.addEventListener("click", (event) => { if (!event.target.closest("button")) open(); });
    row.addEventListener("keydown", (event) => {
      if (event.target === row && (event.key === "Enter" || event.key === " ")) { event.preventDefault(); open(); }
    });
  });
  $$("[data-quick]").forEach((button) => button.addEventListener("click", () => runTask(devices[button.dataset.quick], "截图", "模拟截图完成 · 未生成实际文件")));
  $("#authorization-help")?.addEventListener("click", () => showGuide(true));
  $("#empty-action")?.addEventListener("click", () => state.scenario === "error" ? showSettings() : showGuide());
  $("#back-button")?.addEventListener("click", () => { state.selected = null; render(); $("[data-device]")?.focus({ preventScroll: true }); });
  $("#copy-serial")?.addEventListener("click", () => copyText(currentDevice().serial));
  $$("[data-action]").forEach((button) => button.addEventListener("click", () => handleAction(button.dataset.action)));
}

function openDevice(id) {
  const device = currentDevices().find((item) => item.id === id);
  if (!device) return;
  if (device.state === "unauthorized") { showGuide(true); return; }
  if (device.state === "offline") {
    openDialog("设备暂时离线", `${deviceSummary(device)}<p class="dialog-copy">ADB 发现了这台设备，但暂时无法通信。可检查数据线、解锁手机或重新连接；网络设备请检查网络连接。离线期间不开放设备命令。</p><div class="dialog-buttons"><button class="primary-button" id="offline-done" data-focus>知道了</button></div>`);
    $("#offline-done").addEventListener("click", closeDialog);
    return;
  }
  state.selected = id;
  render();
  $("#back-button").focus({ preventScroll: true });
}

function changeScenario(scenario) {
  const interrupted = Boolean(state.task);
  state.generation++;
  state.task = null;
  state.scenario = scenario;
  state.selected = null;
  fileTarget = null;
  render();
  footer(interrupted ? "场景已变化，未完成的模拟任务已取消" : scenario === "error" ? "ADB 服务连接失败（模拟）" : scenario === "empty" ? "正在等待设备连接" : "设备状态已同步", interrupted ? "amber" : scenario === "error" ? "red" : scenario === "empty" ? "gray" : "green");
}

function runTask(device, action, message) {
  if (!currentDevices().some((item) => item.id === device?.id && item.state === "online")) {
    notify("设备不在线，未执行操作。");
    return;
  }
  if (state.task) { notify("已有模拟任务执行中，请稍候。"); return; }
  const task = { deviceId: device.id, generation: state.generation };
  state.task = task;
  footer(`正在为 ${device.name} 模拟${action}…`, "green", true);
  render();
  setTimeout(() => {
    if (state.task !== task || task.generation !== state.generation) return;
    state.task = null;
    if (!currentDevices().some((item) => item.id === device.id)) {
      footer("设备已断开，模拟任务未完成", "amber");
    } else {
      footer(message);
      notify(`${device.name}：${message}`);
    }
    render();
  }, action === "安装" ? 1600 : 950);
}

function handleAction(action) {
  const device = currentDevice();
  if (!device) return;
  if (action === "screenshot") runTask(device, "截图", "模拟截图完成 · 未生成实际文件");
  if (action === "install") {
    fileTarget = { deviceId: device.id, generation: state.generation };
    filePickerOpen = true;

    $("#apk-input").value = "";
    $("#apk-input").click();
  }
  if (action === "keys") showKeys(device);
  if (action === "info") showInfo(device);
  if (action === "logcat") showLogcat(device);
  if (action === "mirror") runTask(device, "启动 scrcpy 投屏", "已模拟启动 scrcpy · 未打开真实投屏窗口");
}

function openDialog(title, body) {

  if (!dialog.open) dialogReturnFocus = document.activeElement;
  $("#dialog-title").textContent = title;
  $("#dialog-body").innerHTML = body;
  if (!dialog.open) dialog.showModal();
  requestAnimationFrame(() => ($("[data-focus]", dialog) || $("#dialog-close")).focus({ preventScroll: true }));
}

function closeDialog() {
  if (dialog.open) dialog.close();
}

function deviceSummary(device) {
  return `<div class="dialog-device">${icon("device")}<div><strong>${escapeHTML(device.name)}</strong><small>${escapeHTML(device.serial)}</small></div></div>`;
}

function showGuide(authorization = false) {
  openDialog(authorization ? "还差手机上的一次确认" : "让你的设备出现在这里", `<p class="dialog-copy">${authorization ? "ADB 已经发现这台设备，但尚未获得调试授权。电脑端无法代替你确认授权。" : "通过 USB 连接，通常只需要下面三个步骤。已连接的网络设备和模拟器也会自动出现在列表中。"}</p><ol class="guide-steps"><li>使用支持数据传输的 USB 线连接设备，保持手机解锁。</li><li>在手机的开发者选项中开启「USB 调试」。</li><li>出现「允许 USB 调试吗？」时，核对电脑并选择允许。</li></ol><div class="dialog-buttons"><button class="primary-button" id="guide-done" data-focus>知道了</button></div>`);
  $("#guide-done").addEventListener("click", closeDialog);
}

function showKeys(device) {
  openDialog("常用按键控制", `${deviceSummary(device)}<p class="dialog-copy">仅模拟操作反馈，不会向手机发送按键。</p><div class="choice-list">${[["Home", "回到桌面"], ["返回", "返回上一层"], ["最近任务", "查看后台任务"], ["亮屏", "唤醒设备屏幕"]].map(([key, hint]) => `<button data-key="${key}">${icon("grid")}<span>${key}</span><small>${hint}</small></button>`).join("")}</div>`);
  $$("[data-key]", dialog).forEach((button) => button.addEventListener("click", () => {
    closeDialog();
    runTask(device, "按键", `模拟按键完成 · ${button.dataset.key}`);
  }));
}

function showInfo(device) {
  const rows = [["设备名称", device.name], ["序列号", device.serial], ["系统版本", device.android], ["设备型号", device.model], ["连接方式", device.connection], ["数据来源", "原型内置模拟数据"]];
  openDialog("设备信息", `<table class="info-table"><tbody>${rows.map(([key, value]) => `<tr><th scope="row">${key}</th><td>${escapeHTML(value)}</td></tr>`).join("")}</tbody></table><div class="dialog-buttons"><button class="secondary-button" id="copy-info">${icon("copy")}复制信息</button><button class="primary-button" id="info-done" data-focus>完成</button></div>`);
  $("#copy-info").addEventListener("click", () => copyText(rows.map((row) => row.join("：")).join("\n")));
  $("#info-done").addEventListener("click", closeDialog);
}

function showLogcat(device) {
  const logs = `--------- 演示日志 / 非真实设备输出 ---------\n14:32:06.138 I ActivityTaskManager: START MainActivity\n14:32:06.201 D AppCompatDelegate: Configuration ready\n14:32:06.319 I ViewRootImpl: Window focus changed\n14:32:06.422 D RenderThread: Frame completed\n14:32:07.014 I ADBDockDemo: Device is ready\n14:32:07.360 D Choreographer: Input event received\n14:32:08.081 I ADBDockDemo: All systems nominal`;
  openDialog("Logcat 日志预览", `${deviceSummary(device)}<p class="dialog-copy">固定演示日志。原生客户端将提供实时订阅、暂停和导出。</p><pre class="log-output">${escapeHTML(logs)}</pre><div class="dialog-buttons"><button class="secondary-button" id="copy-log">${icon("copy")}复制日志</button><button class="primary-button" id="log-done" data-focus>关闭</button></div>`);
  $("#copy-log").addEventListener("click", () => copyText(logs));
  $("#log-done").addEventListener("click", closeDialog);
}

function showSettings() {
  openDialog("偏好设置", `<p class="dialog-copy">浏览器仅模拟悬浮窗行为，无法设置系统级置顶或调用本机 ADB。下列设置仅影响本次预览。</p><div class="dialog-settings"><label class="switch-row"><span>悬浮窗置顶<small>正式版将使用 Windows 原生窗口能力</small></span><input id="setting-topmost" type="checkbox" role="switch" ${state.topmost ? "checked" : ""}><span class="switch-track" aria-hidden="true"></span></label><label class="field-label" for="adb-path">ADB 路径（示意，只读）</label><input class="field-input" id="adb-path" value="C:\\Android\\platform-tools\\adb.exe" readonly><label class="field-label" for="scrcpy-path">scrcpy 路径（示意，只读）</label><input class="field-input" id="scrcpy-path" value="C:\\Tools\\scrcpy\\scrcpy.exe" readonly></div><div class="dialog-buttons"><button class="secondary-button" id="restore-demo">恢复正常场景</button><button class="primary-button" id="save-settings" data-focus>完成</button></div>`);
  $("#setting-topmost").addEventListener("change", (event) => { state.topmost = event.target.checked; notify("已记录预览偏好；浏览器无法改变系统置顶。"); });
  $("#restore-demo").addEventListener("click", () => { closeDialog(); changeScenario("healthy"); notify("已切换为两台设备在线的模拟场景。"); });
  $("#save-settings").addEventListener("click", closeDialog);
}

async function copyText(text) {
  try {
    if (!navigator.clipboard?.writeText) throw new Error("Clipboard unavailable");
    await navigator.clipboard.writeText(text);
    notify("已复制到剪贴板");
  } catch {
    notify("浏览器未允许复制，请在本地 HTTP 预览中重试。");
  }
}

function setPinned(pinned) {
  state.pinned = pinned;
  const button = $("#pin-button");
  button.classList.toggle("active", pinned);
  button.setAttribute("aria-pressed", String(pinned));
  button.setAttribute("aria-label", pinned ? "取消固定展开" : "固定展开");
  button.title = pinned ? "取消固定展开" : "固定展开";
}

function bounds() {
  return { maxX: Math.max(0, desktop.clientWidth - dock.offsetWidth), maxY: Math.max(0, desktop.clientHeight - 48 - dock.offsetHeight) };
}

function positionDock(x, y) {
  const { maxX, maxY } = bounds();
  dock.style.left = `${Math.max(0, Math.min(maxX, x))}px`;
  dock.style.top = `${Math.max(8, Math.min(Math.max(8, maxY), y))}px`;
}

function resetPosition() {
  dock.hidden = false;
  state.collapsed = false;
  state.side = null;
  state.edgeOffset = null;
  edgeTab.hidden = true;
  positionDock((desktop.clientWidth - dock.offsetWidth) / 2 + (desktop.clientWidth > 600 ? 30 : 0), (desktop.clientHeight - 48 - dock.offsetHeight) / 2);
  $("#desktop-tip").lastElementChild.textContent = "支持四边停靠 · 收起后长按ADB 和数量区域调整位置";
}

function collapse(manual = false) {
  if (state.collapsed || dialog.open || filePickerOpen || drag || edgePress) return;
  if (!manual && (state.pinned || !state.side)) return;
  if (manual) setPinned(false);
  const { maxX } = bounds();
  state.side ||= dock.offsetLeft < maxX / 2 ? "left" : "right";
  const horizontal = state.side === "top" || state.side === "bottom";
  const offset = state.edgeOffset ?? (horizontal ? dock.offsetLeft : dock.offsetTop + 110);
  const restoreFocus = dock.contains(document.activeElement);
  state.collapsed = true;
  dock.hidden = true;
  edgeTab.hidden = false;
  renderEdge();
  positionEdge(offset);
  $("#view-caption").textContent = "贴边状态";
  $("#desktop-tip").lastElementChild.textContent = "长按ADB 和数量区域移到四边 · 点击设备图标操作";
  if (restoreFocus) $("#edge-expand-button").focus({ preventScroll: true });
}

function expand(focus = false) {
  if (!state.collapsed || edgePress) return;
  const centerX = edgeTab.offsetLeft + edgeTab.offsetWidth / 2;
  const centerY = edgeTab.offsetTop + edgeTab.offsetHeight / 2;
  state.collapsed = false;
  dock.hidden = false;
  edgeTab.hidden = true;
  const { maxX, maxY } = bounds();
  const x = state.side === "left" ? 0 : state.side === "right" ? maxX : centerX - dock.offsetWidth / 2;
  const y = state.side === "top" ? 8 : state.side === "bottom" ? maxY : centerY - dock.offsetHeight / 2;
  positionDock(x, y);
  $("#view-caption").textContent = state.selected ? "设备操作" : "设备面板";
  $("#desktop-tip").lastElementChild.textContent = "卡片保持显示 · 拖到屏幕边缘松开后收起";
  if (focus) $("#collapse-button").focus({ preventScroll: true });
}

const edgeSummary = $("#edge-expand-button");
edgeSummary.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) return;


  suppressEdgeClick = false;
  const press = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, left: edgeTab.offsetLeft, top: edgeTab.offsetTop, side: state.side, offset: state.edgeOffset, active: false, moved: false };
  edgePress = press;
  edgeSummary.focus({ preventScroll: true });
  edgeSummary.setPointerCapture(event.pointerId);
  longPressTimer = setTimeout(() => {
    if (edgePress !== press) return;
    press.active = true;
    desktop.classList.add("edge-dragging");
    edgeTab.classList.add("is-dragging");
    $("#desktop-tip").lastElementChild.textContent = "拖到想要的位置，松手吸附最近的一边";
  }, 350);
  event.preventDefault();
});
edgeSummary.addEventListener("pointermove", (event) => {
  const press = edgePress;
  if (!press || event.pointerId !== press.pointerId) return;
  const dx = event.clientX - press.x;
  const dy = event.clientY - press.y;
  if (!press.active) {
    if (Math.hypot(dx, dy) > 6) {
      clearTimeout(longPressTimer);
      press.moved = true;
    }
    return;
  }
  press.moved ||= Math.hypot(dx, dy) > 3;
  edgeTab.style.left = `${Math.max(0, Math.min(desktop.clientWidth - edgeTab.offsetWidth, press.left + dx))}px`;
  edgeTab.style.right = "auto";
  edgeTab.style.top = `${Math.max(0, Math.min(desktop.clientHeight - 48 - edgeTab.offsetHeight, press.top + dy))}px`;
});
function finishEdgePress(event, cancelled = false) {
  const press = edgePress;
  if (!press) return;
  clearTimeout(longPressTimer);
  edgePress = null;
  suppressEdgeClick = press.active || press.moved || cancelled;
  desktop.classList.remove("edge-dragging");
  edgeTab.classList.remove("is-dragging");
  // 短按时保留命中节点，避免浏览器取消随后的 click。
  if (press.active && press.moved && !cancelled) {
    const rect = desktop.getBoundingClientRect();
    const x = Math.max(0, Math.min(desktop.clientWidth, event.clientX - rect.left));
    const workHeight = desktop.clientHeight - 48;
    const y = Math.max(0, Math.min(workHeight, event.clientY - rect.top));
    const distances = [["left", x], ["right", desktop.clientWidth - x], ["top", y], ["bottom", workHeight - y]];
    state.side = distances.sort((a, b) => a[1] - b[1])[0][0];
    state.edgeOffset = null;
    renderEdge();
    positionEdge(state.side === "top" || state.side === "bottom" ? x - edgeTab.offsetWidth / 2 : y - edgeTab.offsetHeight / 2);
  } else if (press.active || press.moved || cancelled) {
    state.side = press.side;
    state.edgeOffset = press.offset;
    renderEdge();
  }
  $("#desktop-tip").lastElementChild.textContent = "长按ADB 和数量区域移到四边 · 点击设备图标操作";
}
edgeSummary.addEventListener("pointerup", (event) => finishEdgePress(event));
edgeSummary.addEventListener("pointercancel", (event) => finishEdgePress(event, true));
edgeSummary.addEventListener("lostpointercapture", (event) => finishEdgePress(event, true));
edgeSummary.addEventListener("click", (event) => {
  const suppress = suppressEdgeClick && event.detail !== 0;
  suppressEdgeClick = false;
  if (suppress || edgePress) return;
  state.selected = null;
  render();
  expand(true);
});
edgeSummary.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && edgePress) { finishEdgePress(event, true); return; }
  const side = { ArrowLeft: "left", ArrowRight: "right", ArrowUp: "top", ArrowDown: "bottom" }[event.key];
  if (!side || edgePress) return;
  event.preventDefault();
  state.side = side;
  state.edgeOffset = null;
  renderEdge();
});

$("#drag-handle").addEventListener("pointerdown", (event) => {
  if (event.button !== 0 || event.target.closest("button")) return;

  drag = { x: event.clientX, y: event.clientY, left: dock.offsetLeft, top: dock.offsetTop };
  $("#drag-handle").setPointerCapture(event.pointerId);
  desktop.classList.add("dragging");
  event.preventDefault();
});
$("#drag-handle").addEventListener("pointermove", (event) => {
  if (!drag) return;
  positionDock(drag.left + event.clientX - drag.x, drag.top + event.clientY - drag.y);
});
function finishDrag(event) {
  if (!drag) return;
  const previous = drag;
  drag = null;
  desktop.classList.remove("dragging");
  if (event.type === "pointercancel") { positionDock(previous.left, previous.top); return; }
  const { maxX, maxY } = bounds();
  const distances = [[Math.abs(dock.offsetLeft), "left"], [Math.abs(maxX - dock.offsetLeft), "right"], [Math.abs(dock.offsetTop - 8), "top"], [Math.abs(maxY - dock.offsetTop), "bottom"]].sort((a, b) => a[0] - b[0]);
  state.side = distances[0][0] <= 20 ? distances[0][1] : null;
  state.edgeOffset = null;
  if (state.side) {
    positionDock(state.side === "left" ? 0 : state.side === "right" ? maxX : dock.offsetLeft, state.side === "top" ? 8 : state.side === "bottom" ? maxY : dock.offsetTop);
    collapse();
  }
}
$("#drag-handle").addEventListener("pointerup", finishDrag);
$("#drag-handle").addEventListener("pointercancel", finishDrag);
const edgeOpenButton = $("#edge-open-button");
edgeOpenButton.addEventListener("click", () => { state.selected = null; render(); expand(true); });
edgeTab.addEventListener("click", (event) => {
  const deviceButton = event.target.closest("[data-edge-device]");
  if (deviceButton) {
    const id = deviceButton.dataset.edgeDevice;
    state.selected = null;
    render();
    expand();
    openDevice(id);
    return;
  }
  if (event.target.closest("[data-edge-more]")) {
    state.selected = null;
    render();
    expand(true);
  }
  const placeholder = event.target.closest("[data-edge-placeholder]");
  if (placeholder) {
    expand();
    if (placeholder.dataset.edgePlaceholder === "error") showSettings();
    else showGuide();
  }
});
$("#collapse-button").addEventListener("click", () => collapse(true));
$("#edge-demo-button").addEventListener("click", () => {
  if (!state.collapsed) { state.side = "right"; state.edgeOffset = null; positionDock(bounds().maxX, dock.offsetTop); }
  collapse(true);
  notify("长按ADB 和数量区域可拖到四边；左右竖排、上下横排。");
});
$("#settings-button").addEventListener("click", showSettings);
$("#pin-button").addEventListener("click", () => setPinned(!state.pinned));
$$("[data-scenario]").forEach((button) => button.addEventListener("click", () => changeScenario(button.dataset.scenario)));
$("#dialog-close").addEventListener("click", closeDialog);
dialog.addEventListener("close", () => {
  if (dialogReturnFocus?.isConnected && !dock.hidden) dialogReturnFocus.focus({ preventScroll: true });
  dialogReturnFocus = null;

});
dialog.addEventListener("click", (event) => {
  const rect = dialog.getBoundingClientRect();
  if (event.target === dialog && (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom)) closeDialog();
});
$("#apk-input").addEventListener("change", (event) => {
  filePickerOpen = false;
  const file = event.target.files[0];
  const target = fileTarget;
  fileTarget = null;
  if (!file || !target) {  return; }
  if (!file.name.toLowerCase().endsWith(".apk")) { notify("请选择 .apk 文件。该文件不会上传。");  return; }
  const device = currentDevices().find((item) => item.id === target.deviceId);
  if (!device || target.generation !== state.generation) { notify("设备状态已变化，已取消模拟安装。");  return; }
  runTask(device, "安装", `模拟安装完成 · ${file.name}（未上传、未安装）`);

});
$("#apk-input").addEventListener("cancel", () => { filePickerOpen = false; fileTarget = null;  });
$("#reset-button").addEventListener("click", () => {


  closeDialog();
  state.topmost = true;
  setPinned(false);
  resetPosition();
  changeScenario("mixed");
  notify("已恢复初始预览。");
});
new ResizeObserver(() => {
  if (!initialized) return;
  if (state.collapsed) {
    renderEdge();
    return;
  }
  positionDock(state.side === "left" ? 0 : state.side === "right" ? bounds().maxX : dock.offsetLeft, state.side === "top" ? 8 : state.side === "bottom" ? bounds().maxY : dock.offsetTop);
}).observe(desktop);
render();
resetPosition();
initialized = true;
footer("设备状态已同步");
