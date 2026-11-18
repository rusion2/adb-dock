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
const commonActions = [["mirror", "mirror", "设备投屏", ""], ["screenshot", "camera", "一键截图", "primary-action"], ["install", "install", "安装 APK", ""], ["keys", "grid", "按键控制", ""], ["info", "info", "设备信息", ""], ["logcat", "log", "查看日志", ""]];
const desktop = $("#desktop");
const desktopTaskbar = $(".desktop-taskbar", desktop);
const dock = $("#dock");
const edgeTab = $("#edge-tab");
const dialog = $("#action-dialog");
const edgePositionKey = "adb-dock-edge-position";
const state = { scenario: "mixed", selected: null, showFloatingTab: true, autoCollapseEnabled: true, collapseDelaySeconds: 2, collapsed: false, hiddenToTray: false, edgePosition: readEdgePosition(), panelSide: null, topmost: true, tasks: new Map(), generation: 0 };
const remarks = new Map(currentDeviceRemarks());
let toastTimer;
let drag = null;
let edgePress = null;
let iconSwipe = null;
let filePickerOpen = false;
let fileTarget = null;
let dialogReturnFocus = null;
let initialized = false;
let collapseTimer = null;
let pointerPosition = null;
let pointerOutsideDocument = false;
let openerBounds = null;
let contextMenuOpen = false;

function stopAutoCollapse() {
  clearTimeout(collapseTimer);
  collapseTimer = null;
}

function pointerInsideBounds(...rectangles) {
  if (pointerOutsideDocument) return false;
  if (!pointerPosition) return true;
  const inside = (rect) => rect && pointerPosition.x >= rect.left && pointerPosition.x <= rect.right && pointerPosition.y >= rect.top && pointerPosition.y <= rect.bottom;
  return rectangles.some(inside);
}

function pointerInsidePanel() {
  return pointerInsideBounds(dock.getBoundingClientRect(), openerBounds);
}

function refreshAutoCollapse(freshGrace = false) {
  if (freshGrace) stopAutoCollapse();
  const paused = state.collapsed || state.hiddenToTray || !state.showFloatingTab || !state.autoCollapseEnabled || dialog.open || filePickerOpen || drag || edgePress || iconSwipe || contextMenuOpen;
  if (paused || pointerInsidePanel()) { stopAutoCollapse(); return; }
  if (collapseTimer !== null) return;
  collapseTimer = setTimeout(() => {
    collapseTimer = null;
    if (!state.collapsed && !state.hiddenToTray && state.showFloatingTab && state.autoCollapseEnabled && !dialog.open && !filePickerOpen && !drag && !edgePress && !iconSwipe && !contextMenuOpen && !pointerInsidePanel()) collapse();
  }, state.collapseDelaySeconds * 1000);
}

function recordPointer(event) {
  pointerPosition = { x: event.clientX, y: event.clientY };
  pointerOutsideDocument = false;
}

function handlePointer(event) {
  recordPointer(event);
  const menuWasOpen = contextMenuOpen;
  contextMenuOpen = false;
  refreshAutoCollapse(menuWasOpen);
}
["pointermove", "pointerdown", "pointerover"].forEach((type) => document.addEventListener(type, handlePointer, true));
document.addEventListener("pointerout", (event) => {
  if (event.relatedTarget !== null) return;
  pointerOutsideDocument = true;
  refreshAutoCollapse();
});
document.addEventListener("contextmenu", (event) => {
  if (!dock.contains(event.target) && !edgeTab.contains(event.target)) return;
  contextMenuOpen = true;
  refreshAutoCollapse(true);
});
document.addEventListener("keydown", () => {
  if (!contextMenuOpen) return;
  contextMenuOpen = false;
  refreshAutoCollapse(true);
});
window.addEventListener("focus", () => {
  const menuWasOpen = contextMenuOpen;
  contextMenuOpen = false;
  refreshAutoCollapse(menuWasOpen);
});

function currentDevices() {
  return scenarios[state.scenario].map((id) => devices[id]);
}

function currentDevice() {
  return currentDevices().find((device) => device.id === state.selected);
}

function workAreaHeight() {
  return desktop.clientHeight - desktopTaskbar.offsetHeight;
}

function isHorizontalEdge(side) {
  return side === "top" || side === "bottom";
}

function defaultEdgeOffset(side) {
  return Math.round((isHorizontalEdge(side) ? desktop.clientWidth : workAreaHeight()) / 3);
}

function readEdgePosition() {
  try {
    const stored = JSON.parse(localStorage.getItem(edgePositionKey));
    if (["left", "right", "top", "bottom"].includes(stored?.side) &&
        Number.isFinite(stored.offset) && stored.offset >= 0) return stored;
  } catch {
    // The prototype can still be used when browser storage is unavailable.
  }
  return { side: "right", offset: defaultEdgeOffset("right") };
}

function setEdgePosition(side, offset) {
  state.edgePosition = { side, offset: Math.max(0, offset) };
  try {
    localStorage.setItem(edgePositionKey, JSON.stringify(state.edgePosition));
  } catch {
    // Keep the new position for this page even if storage is unavailable.
  }
}

function currentDeviceRemarks() {
  try {
    const stored = JSON.parse(localStorage.getItem("adb-dock-device-remarks"));
    return Object.values(devices)
      .filter((device) => typeof stored?.[device.serial] === "string")
      .map((device) => [device.serial, stored[device.serial].slice(0, 24)]);
  } catch {
    return [];
  }
}

function deviceDisplayName(device) {
  const remark = remarks.get(device.serial);
  return remark ? `${remark} · ${device.name}` : device.name;
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
  const name = escapeHTML(deviceDisplayName(device));
  return `<div class="device-row ${index === 0 ? "primary" : ""}" role="button" tabindex="0" data-device="${device.id}" aria-label="${name}，${status.label}，${online ? "查看操作" : "查看连接指引"}">
    <span class="device-avatar ${device.avatar}">${icon("device")}</span>
    <div class="device-identity"><div class="device-name-line"><span class="device-name" title="${name}">${name}</span><span class="connection-badge">${device.connection === "网络" ? icon("wifi") : ""}${device.connection}</span></div><div class="device-serial">${device.serial}</div></div>
    <div class="device-end"><span class="device-state ${online ? "" : device.state === "offline" ? "offline" : "warning"}"><span class="status-dot ${status.tone}"></span>${status.label}</span><span class="row-action"><button class="quick-action" type="button" data-remark="${device.id}" aria-label="编辑 ${name} 的备注" title="编辑设备备注">${icon("edit")}</button>${online ? `<button class="quick-action" type="button" data-quick="${device.id}" ${state.tasks.has(device.id) ? "disabled" : ""} aria-label="对 ${name} 模拟一键截图" title="一键截图">${icon("camera")}</button>` : "查看指引"}${icon("chevron")}</span></div>
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
  const disabled = state.tasks.has(device.id) ? "disabled" : "";
  return `<div class="detail-heading"><button class="icon-button" id="back-button" aria-label="返回设备列表">${icon("back")}</button><h2>设备操作</h2>${deviceSelectorMarkup(device.id)}</div>
    <div class="device-detail-meta"><span class="detail-serial" title="${escapeHTML(device.serial)}">${escapeHTML(device.serial)}</span><span class="connection-badge">${device.connection === "网络" ? icon("wifi") : ""}${device.connection}</span><button class="icon-button" id="copy-serial" type="button" aria-label="复制设备序列号" title="复制序列号">${icon("copy")}</button><button class="secondary-button remark-button" type="button" data-remark="${device.id}" aria-label="编辑设备备注">${icon("edit")}备注</button></div>
    <div class="action-grid">${commonActions.map(([action, image, label, className]) => `<button class="device-action ${className}" type="button" data-action="${action}" ${disabled}>${icon(image)}<span>${label}</span></button>`).join("")}</div>
    <div class="detail-tools"><button class="text-button" type="button" data-action="screenshot-as" ${disabled}>截图另存为…</button></div>`;
}

function deviceSelectorMarkup(selected) {
  const options = currentDevices().filter((device) => device.state === "online");
  const selectedName = escapeHTML(deviceDisplayName(devices[selected]));
  return `<select class="device-selector" id="operation-device-select" aria-label="切换操作设备" title="${selectedName}">${options.map((device) => `<option value="${escapeHTML(device.id)}" ${device.id === selected ? "selected" : ""}>${escapeHTML(deviceDisplayName(device))} · ${escapeHTML(device.serial)}</option>`).join("")}</select>`;
}

function bindDeviceSelector(select, change) {
  if (!select) return;
  select.addEventListener("pointerdown", () => { contextMenuOpen = true; refreshAutoCollapse(true); });
  select.addEventListener("keydown", (event) => {
    if (event.key !== " " && !(event.altKey && event.key === "ArrowDown")) return;
    contextMenuOpen = true;
    refreshAutoCollapse(true);
  });
  select.addEventListener("change", () => {
    contextMenuOpen = false;
    change(select.value);
    refreshAutoCollapse(true);
  });
  select.addEventListener("blur", () => { contextMenuOpen = false; refreshAutoCollapse(true); });
}

function render() {
  const device = currentDevice();
  const operationView = Boolean(device);
  if (dock.classList.contains("operation-view") !== operationView) {
    dock.classList.toggle("operation-view", operationView);
    if (!dock.hidden) positionDock(dock.offsetLeft, state.panelSide === "bottom" ? bounds().maxY : dock.offsetTop);
  }
  $("#dock-content").innerHTML = device ? detailMarkup(device) : listMarkup();
  $("#view-caption").textContent = state.hiddenToTray ? "托盘隐藏" : state.collapsed ? "贴边状态" : device ? "设备操作" : "设备面板";
  $$(".scenario").forEach((button) => {
    const selected = button.dataset.scenario === state.scenario;
    button.classList.toggle("selected", selected);
    button.setAttribute("aria-pressed", selected);
  });
  renderEdge();
  bindContent();
}

function renderEdge() {
  if (!state.collapsed || edgePress?.active) return;
  const list = currentDevices();
  const online = list.filter((device) => device.state === "online").length;
  const error = state.scenario === "error";
  const warning = online !== list.length;
  const hasOffline = list.some((device) => device.state === "offline");
  const tone = error ? "error" : !list.length ? "empty" : warning ? "warning" : "good";
  const { side, offset } = state.edgePosition;
  const horizontal = isHorizontalEdge(side);
  edgeTab.className = `edge-tab tone-${tone} side-${side}${horizontal ? " horizontal" : ""}`;
  $("#edge-status").textContent = error ? "异常" : !list.length ? "未连接" : hasOffline ? "有离线" : warning ? "待授权" : "在线";
  $("#edge-count").innerHTML = error ? "—" : `${online}<span>/${list.length}</span>`;
  $(".edge-state .status-dot").className = `status-dot ${error ? "red" : !list.length ? "gray" : warning ? "amber" : "green"}`;
  const description = error ? "ADB 异常，设备数量未知" : `${online} 台在线，共发现 ${list.length} 台${warning ? "，有设备需要处理" : ""}`;
  edgeTab.setAttribute("aria-label", `${description}；每个图标对应一台设备，按住 ADB 和数量区域并移动可拖到四边`);
  $("#edge-expand-button").setAttribute("aria-label", `${description}，仅用于拖动，点击不展开，按住移动即拖动，方向键切换停靠边`);
  const edgeList = $("#edge-device-list");
  edgeList.innerHTML = `<button class="edge-placeholder ${error ? "error" : ""}" data-edge-placeholder="${error ? "error" : "empty"}" aria-label="${error ? "ADB 异常，检查设置" : "未发现设备，查看连接指引"}">${icon(error ? "warning" : "usb")}</button>`;
  const axisLength = horizontal ? desktop.clientWidth : workAreaHeight();
  const minimumLength = horizontal ? edgeTab.offsetWidth : edgeTab.offsetHeight;
  const margin = Math.min(8, Math.max(0, (axisLength - minimumLength) / 2));
  const visibleOffset = Math.max(margin, Math.min(axisLength - minimumLength - margin, offset));
  const availableLength = axisLength - margin - visibleOffset;
  if (list.length) {
    // Each additional button uses 34 pixels and a 5-pixel gap. The +N button takes a slot.
    const slots = 1 + Math.floor(Math.max(0, availableLength - minimumLength) / 39);
    const capacity = Math.min(5, slots);
    const visibleCount = list.length > capacity ? Math.min(5, slots - 1) : capacity;
    edgeList.innerHTML = list.slice(0, visibleCount).map((device) => {
      const status = deviceStates[device.state];
      const name = deviceDisplayName(device);
      const remark = remarks.get(device.serial);
      const label = `${name}，${status.label}，${device.connection}，${device.serial}；点击或向左滑动${device.state === "online" ? "打开此设备的操作面板" : "查看连接指引"}`;
      return `<button class="edge-device-button ${device.state}${remark ? " has-remark" : ""}" data-edge-device="${escapeHTML(device.id)}" aria-label="${escapeHTML(label)}">${icon("device")}<span class="edge-device-marker" aria-hidden="true">${status.marker}</span>${remark ? `<span class="edge-device-remark" aria-hidden="true">${escapeHTML(remark)}</span>` : ""}<span class="edge-tooltip" aria-hidden="true"><b>${escapeHTML(name)}</b><em>${status.label} · ${device.connection}</em><small>${escapeHTML(device.serial)}</small></span></button>`;
    }).join("") + (list.length > visibleCount ? `<button class="edge-more" data-edge-more aria-label="另有 ${list.length - visibleCount} 台设备，展开全部设备">+${list.length - visibleCount}</button>` : "");
  }
  edgeTab.style.left = horizontal ? `${visibleOffset}px` : side === "left" ? "0px" : "auto";
  edgeTab.style.right = side === "right" ? "0px" : "auto";
  edgeTab.style.top = `${horizontal ? side === "top" ? 0 : Math.max(0, workAreaHeight() - edgeTab.offsetHeight) : visibleOffset}px`;
}

function bindContent() {
  bindDeviceSelector($("#operation-device-select"), (id) => {
    if (!currentDevices().some((device) => device.id === id && device.state === "online")) return;
    state.selected = id;
    render();
    $("#operation-device-select").focus({ preventScroll: true });
  });
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
  $$("[data-quick]").forEach((button) => button.addEventListener("click", () => captureScreenshot(devices[button.dataset.quick])));
  $$("[data-remark]").forEach((button) => button.addEventListener("click", () => editDeviceRemark(devices[button.dataset.remark])));
  $("#authorization-help")?.addEventListener("click", () => showGuide(true));
  $("#empty-action")?.addEventListener("click", () => state.scenario === "error" ? showSettings() : showGuide());
  $("#back-button")?.addEventListener("click", () => { state.selected = null; render(); $("[data-device]")?.focus({ preventScroll: true }); });
  $("#copy-serial")?.addEventListener("click", () => copyText(currentDevice().serial));
  $$("[data-action]").forEach((button) => button.addEventListener("click", () => handleAction(button.dataset.action)));
}

function openDevice(id) {
  const device = currentDevices().find((item) => item.id === id);
  if (!device) return;
  const online = device.state === "online";
  if (online || state.collapsed) {
    state.selected = online ? id : null;
    render();
    expand();
    $(online ? "#back-button" : `[data-device="${id}"]`)?.focus({ preventScroll: true });
  }
  if (device.state === "unauthorized") { showGuide(true, device); return; }
  if (device.state === "offline") {
    openDialog("设备暂时离线", `${deviceSummary(device)}<p class="dialog-copy">ADB 发现了这台设备，但暂时无法通信。可检查数据线、解锁手机或重新连接；网络设备请检查网络连接。离线期间不开放设备命令。</p><div class="dialog-buttons"><button class="primary-button" id="offline-done" data-focus>知道了</button></div>`);
    $("#offline-done").addEventListener("click", closeDialog);
  }
}

function changeScenario(scenario) {
  const interrupted = state.tasks.size > 0;
  state.generation++;
  state.tasks.clear();
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
  if (state.tasks.has(device.id)) { notify("这台设备已有模拟任务执行中，请稍候。"); return; }
  const generation = state.generation;
  const progress = `正在为 ${device.name} 模拟${action}…`;
  state.tasks.set(device.id, progress);
  footer(progress, "green", true);
  render();
  setTimeout(() => {
    if (generation !== state.generation) return;
    state.tasks.delete(device.id);
    if (!currentDevices().some((item) => item.id === device.id)) {
      footer("设备已断开，模拟任务未完成", "amber");
    } else {
      footer(message);
      notify(`${device.name}：${message}`);
    }
    if (state.tasks.size) footer(state.tasks.values().next().value, "green", true);
    render();
  }, action === "安装" ? 1600 : 950);
}

function handleAction(action, device = currentDevice()) {
  if (!device) return;
  if (action === "screenshot") captureScreenshot(device);
  if (action === "screenshot-as") showScreenshotSaveAs(device);
  if (action === "install") {
    fileTarget = { deviceId: device.id, generation: state.generation };
    filePickerOpen = true;
    refreshAutoCollapse(true);

    $("#apk-input").value = "";
    $("#apk-input").click();
  }
  if (action === "keys") showKeys(device);
  if (action === "info") showInfo(device);
  if (action === "logcat") showLogcat(device);
  if (action === "mirror") runTask(device, "启动 scrcpy 投屏", "已模拟启动 scrcpy · 未打开真实投屏窗口");
}

function openDialog(title, body) {
  stopAutoCollapse();
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
  return `<div class="dialog-device">${icon("device")}<div><strong>${escapeHTML(deviceDisplayName(device))}</strong><small>${escapeHTML(device.serial)}</small></div></div>`;
}

function showGuide(authorization = false, device = null) {
  openDialog(authorization ? "还差手机上的一次确认" : "让你的设备出现在这里", `${device ? deviceSummary(device) : ""}<p class="dialog-copy">${authorization ? "ADB 已经发现这台设备，但尚未获得调试授权。电脑端无法代替你确认授权。" : "通过 USB 连接，通常只需要下面三个步骤。已连接的网络设备和模拟器也会自动出现在列表中。"}</p><ol class="guide-steps"><li>使用支持数据传输的 USB 线连接设备，保持手机解锁。</li><li>在手机的开发者选项中开启「USB 调试」。</li><li>出现「允许 USB 调试吗？」时，核对电脑并选择允许。</li></ol><div class="dialog-buttons"><button class="primary-button" id="guide-done" data-focus>知道了</button></div>`);
  $("#guide-done").addEventListener("click", closeDialog);
}

function editDeviceRemark(device) {
  const remark = remarks.get(device.serial) ?? "";
  openDialog("编辑设备备注", `${deviceSummary(device)}<p class="dialog-copy">备注会作为贴边浮窗的短标签，最多 24 个字符；留空即可移除。</p><form id="remark-form"><label class="field-label" for="device-remark-input">设备备注</label><input class="field-input" id="device-remark-input" maxlength="24" value="${escapeHTML(remark)}" data-focus><div class="dialog-buttons"><button class="secondary-button" type="button" id="cancel-remark">取消</button><button class="primary-button" type="submit">保存</button></div></form>`);
  $("#cancel-remark").addEventListener("click", closeDialog);
  $("#remark-form").addEventListener("submit", (event) => {
    event.preventDefault();
    const value = $("#device-remark-input").value.trim();
    if (value) remarks.set(device.serial, value);
    else remarks.delete(device.serial);
    try {
      localStorage.setItem("adb-dock-device-remarks", JSON.stringify(Object.fromEntries(remarks)));
    } catch {
      notify("浏览器限制了本地存储，备注会保留到页面关闭。");
    }
    closeDialog();
    render();
    $(`[data-remark="${device.id}"]`).focus({ preventScroll: true });
  });
}

function screenshotFileName() {
  return `screenshot-${new Date().toISOString().replace(/[:.]/g, "-")}.png`;
}

function captureScreenshot(device, fileName = screenshotFileName()) {
  runTask(device, "截图", `模拟截图完成 · ${fileName}（未生成实际文件）`);
}

function showScreenshotSaveAs(device) {
  openDialog("截图另存为", `${deviceSummary(device)}<form id="screenshot-form"><label class="field-label" for="screenshot-file-name">文件名</label><input class="field-input" id="screenshot-file-name" maxlength="128" value="${screenshotFileName()}" data-focus><div class="dialog-buttons"><button class="secondary-button" type="button" id="cancel-screenshot">取消</button><button class="primary-button" type="submit">模拟保存</button></div></form>`);
  $("#cancel-screenshot").addEventListener("click", closeDialog);
  $("#screenshot-form").addEventListener("submit", (event) => {
    event.preventDefault();
    const name = $("#screenshot-file-name").value.trim() || screenshotFileName();
    closeDialog();
    captureScreenshot(device, /\.png$/i.test(name) ? name : `${name}.png`);
  });
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
  const logs = ["--------- 模拟日志 ---------", "14:32:06.138 I ActivityTaskManager: START MainActivity", "14:32:06.201 D AppCompatDelegate: Configuration ready", "14:32:06.319 I ViewRootImpl: Window focus changed"];
  const messages = ["D RenderThread: Frame completed", "I ADBDockDemo: Device is ready", "D Choreographer: Input event received"];
  const retainedLogLimit = 500;
  openDialog("Logcat 日志预览", `${deviceSummary(device)}<div class="log-toolbar"><button class="secondary-button" id="pause-log" data-focus>暂停显示</button><span id="log-status">显示中</span></div><pre class="log-output" id="log-output" tabindex="0" aria-label="模拟设备日志"></pre><div class="dialog-buttons"><button class="secondary-button" id="copy-log">${icon("copy")}复制全部日志</button><button class="primary-button" id="log-done">关闭</button></div>`);
  const output = $("#log-output");
  const pause = $("#pause-log");
  const status = $("#log-status");
  let pending = 0;
  let paused = false;
  let nextMessage = 0;
  function displayLogs() {
    output.textContent = `${logs.join("\n")}\n`;
    output.scrollTop = output.scrollHeight;
  }
  displayLogs();
  const timer = setInterval(() => {
    if (!dialog.open || !output.isConnected) { clearInterval(timer); return; }
    const timestamp = new Date().toTimeString().slice(0, 8);
    logs.push(`${timestamp} ${messages[nextMessage++ % messages.length]}`);
    if (logs.length > retainedLogLimit) logs.shift();
    if (paused) {
      pending++;
      status.textContent = pending > retainedLogLimit
        ? `已暂停 · 收到 ${pending} 条 · 保留最近 ${retainedLogLimit} 条`
        : `已暂停 · ${pending} 条待显示`;
    } else {
      displayLogs();
    }
  }, 850);
  dialog.addEventListener("close", () => clearInterval(timer), { once: true });
  pause.addEventListener("click", () => {
    paused = !paused;
    pause.textContent = paused ? "继续显示" : "暂停显示";
    status.textContent = paused ? "已暂停 · 0 条待显示" : "显示中";
    if (!paused) { pending = 0; displayLogs(); }
  });
  $("#copy-log").addEventListener("click", () => copyText(logs.join("\n")));
  $("#log-done").addEventListener("click", closeDialog);
}

function showSettings() {
  openDialog("偏好设置", `<p class="dialog-copy">浏览器仅模拟悬浮窗行为，无法设置系统级置顶或调用本机 ADB。设置仅影响本次页面会话，点击保存后生效；取消或关闭弹窗不应用修改。</p><div class="dialog-settings"><label class="switch-row"><span>悬浮窗置顶<small>正式版将使用 Windows 原生窗口能力</small></span><input id="setting-topmost" type="checkbox" role="switch" ${state.topmost ? "checked" : ""}><span class="switch-track" aria-hidden="true"></span></label><label class="switch-row"><span>自动收起<small>仅在“显示浮窗”开启时生效；关闭自动收起不影响主动拖动收起</small></span><input id="setting-auto-collapse" type="checkbox" role="switch" ${state.autoCollapseEnabled ? "checked" : ""}><span class="switch-track" aria-hidden="true"></span></label><label class="field-label" for="setting-collapse-delay">收起延时（秒，1–300 的整数）</label><input class="field-input" id="setting-collapse-delay" type="number" min="1" max="300" step="1" value="${state.collapseDelaySeconds}" aria-describedby="collapse-settings-error" ${state.autoCollapseEnabled ? "" : "disabled"}><p class="settings-validation" id="collapse-settings-error" role="status"></p><label class="field-label" for="adb-path">ADB 路径（示意，只读）</label><input class="field-input" id="adb-path" value="C:\\Android\\platform-tools\\adb.exe" readonly><label class="field-label" for="scrcpy-path">scrcpy 路径（示意，只读）</label><input class="field-input" id="scrcpy-path" value="C:\\Tools\\scrcpy\\scrcpy.exe" readonly></div><div class="dialog-buttons"><button class="secondary-button" id="restore-demo">恢复正常场景</button><button class="secondary-button" id="cancel-settings">取消</button><button class="primary-button" id="save-settings" data-focus>保存</button></div>`);
  const automaticInput = $("#setting-auto-collapse");
  const delayInput = $("#setting-collapse-delay");
  const validateDelay = () => {
    if (!automaticInput.checked) {
      delayInput.setAttribute("aria-invalid", "false");
      $("#collapse-settings-error").textContent = "";
      return state.collapseDelaySeconds;
    }
    const raw = delayInput.value.trim();
    const seconds = Number(raw);
    const valid = /^\d+$/.test(raw) && Number.isInteger(seconds) && seconds >= 1 && seconds <= 300;
    delayInput.setAttribute("aria-invalid", String(!valid));
    $("#collapse-settings-error").textContent = valid ? "" : "请输入 1–300 之间的整数秒数。";
    return valid ? seconds : null;
  };
  automaticInput.addEventListener("change", () => {
    delayInput.disabled = !automaticInput.checked;
    delayInput.setAttribute("aria-invalid", "false");
    $("#collapse-settings-error").textContent = "";
  });
  delayInput.addEventListener("input", validateDelay);
  $("#restore-demo").addEventListener("click", () => { closeDialog(); changeScenario("healthy"); notify("已切换为两台设备在线的模拟场景。"); });
  $("#cancel-settings").addEventListener("click", closeDialog);
  $("#save-settings").addEventListener("click", () => {
    const seconds = validateDelay();
    if (seconds === null) { delayInput.focus(); return; }
    state.topmost = $("#setting-topmost").checked;
    state.autoCollapseEnabled = automaticInput.checked;
    state.collapseDelaySeconds = seconds;
    updateDesktopTip();
    closeDialog();
  });
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

function updateDesktopTip() {
  $("#desktop-tip").lastElementChild.textContent = state.hiddenToTray
    ? "面板已隐藏到模拟托盘 · 点击任务栏中的 ADB Dock 图标重新打开"
    : state.collapsed ? "点击设备直达操作 · 点击“展开”查看全部设备"
    : !state.showFloatingTab ? "显示浮窗已关闭 · 面板保持显示，关闭后隐藏到模拟托盘"
    : state.autoCollapseEnabled ? `离开面板 ${state.collapseDelaySeconds} 秒后收起 · 主动向边缘拖动可立即收起`
    : "自动收起已关闭 · 可主动向边缘拖动或点击关闭收起";
}

function setShowFloatingTab(enabled) {
  state.showFloatingTab = enabled;
  const button = $("#floating-tab-button");
  button.classList.toggle("active", enabled);
  button.setAttribute("aria-pressed", String(enabled));
  button.setAttribute("aria-label", "显示浮窗");
  button.title = enabled
    ? "显示浮窗已开启：关闭面板时收为贴边浮窗；点击可关闭显示浮窗"
    : "显示浮窗已关闭：面板保持显示，关闭后隐藏到托盘；点击可开启";
  if (!enabled && state.collapsed) expand(true);
  updateDesktopTip();
  refreshAutoCollapse(true);
}

function bounds() {
  return { maxX: Math.max(0, desktop.clientWidth - dock.offsetWidth), maxY: Math.max(0, workAreaHeight() - dock.offsetHeight) };
}

function positionDock(x, y) {
  const { maxX, maxY } = bounds();
  dock.style.left = `${Math.max(0, Math.min(maxX, x))}px`;
  dock.style.top = `${Math.max(0, Math.min(maxY, y))}px`;
}

function resetPosition() {
  stopAutoCollapse();
  openerBounds = null;
  dock.hidden = false;
  state.collapsed = false;
  state.hiddenToTray = false;
  state.panelSide = null;
  edgeTab.hidden = true;
  positionDock((desktop.clientWidth - dock.offsetWidth) / 2 + (desktop.clientWidth > 600 ? 30 : 0), (workAreaHeight() - dock.offsetHeight) / 2);
  updateDesktopTip();
  refreshAutoCollapse(true);
}

function collapse() {
  if (!state.showFloatingTab || state.collapsed || state.hiddenToTray || dialog.open || filePickerOpen || drag || edgePress) return;
  stopAutoCollapse();
  openerBounds = null;
  const restoreFocus = dock.contains(document.activeElement);
  state.collapsed = true;
  state.hiddenToTray = false;
  dock.hidden = true;
  edgeTab.hidden = false;
  renderEdge();
  $("#view-caption").textContent = "贴边状态";
  updateDesktopTip();
  if (restoreFocus) $("#edge-expand-button").focus({ preventScroll: true });
}

function expand(focus = false) {
  if ((!state.collapsed && !state.hiddenToTray) || edgePress) return;
  stopAutoCollapse();
  const fromTab = state.collapsed;
  openerBounds = fromTab ? edgeTab.getBoundingClientRect() : null;
  const centerX = edgeTab.offsetLeft + edgeTab.offsetWidth / 2;
  const centerY = edgeTab.offsetTop + edgeTab.offsetHeight / 2;
  state.collapsed = false;
  state.hiddenToTray = false;
  dock.hidden = false;
  edgeTab.hidden = true;
  if (fromTab) state.panelSide = state.edgePosition.side;
  const side = state.panelSide;
  const { maxX, maxY } = bounds();
  const x = fromTab ? side === "left" ? 0 : side === "right" ? maxX : centerX - dock.offsetWidth / 2 : parseFloat(dock.style.left) || 0;
  const y = fromTab ? side === "top" ? 0 : side === "bottom" ? maxY : centerY - dock.offsetHeight / 2 : parseFloat(dock.style.top) || 0;
  positionDock(x, y);
  $("#view-caption").textContent = state.selected ? "设备操作" : "设备面板";
  updateDesktopTip();
  if (focus) $("#close-panel-button").focus({ preventScroll: true });
  refreshAutoCollapse(true);
}

function closePanel() {
  if (dialog.open || filePickerOpen || drag || edgePress || state.hiddenToTray) return;
  if (state.showFloatingTab) { collapse(); return; }
  stopAutoCollapse();
  openerBounds = null;
  state.collapsed = false;
  state.hiddenToTray = true;
  dock.hidden = true;
  edgeTab.hidden = true;
  $("#view-caption").textContent = "托盘隐藏";
  updateDesktopTip();
  $("#tray-reopen-button").focus({ preventScroll: true });
}

const edgeSummary = $("#edge-expand-button");
edgeSummary.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) return;


  const press = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, left: edgeTab.offsetLeft, top: edgeTab.offsetTop, active: false };
  edgePress = press;
  refreshAutoCollapse(true);
  edgeSummary.focus({ preventScroll: true });
  edgeSummary.setPointerCapture(event.pointerId);
  event.preventDefault();
});
function moveEdgePress(event) {
  const press = edgePress;
  if (!press || event.pointerId !== press.pointerId) return;
  const dx = event.clientX - press.x;
  const dy = event.clientY - press.y;
  if (!press.active) {
    if (Math.hypot(dx, dy) < 4) return;
    press.active = true;
    desktop.classList.add("edge-dragging");
    edgeTab.classList.add("is-dragging");
    $("#desktop-tip").lastElementChild.textContent = "拖到想要的位置，松手吸附最近的一边";
  }
  edgeTab.style.left = `${Math.max(0, Math.min(desktop.clientWidth - edgeTab.offsetWidth, press.left + dx))}px`;
  edgeTab.style.right = "auto";
  edgeTab.style.top = `${Math.max(0, Math.min(workAreaHeight() - edgeTab.offsetHeight, press.top + dy))}px`;
}
edgeSummary.addEventListener("pointermove", moveEdgePress);
function finishEdgePress(event, cancelled = false) {
  const press = edgePress;
  if (!press) return;
  if (!cancelled && event.type === "pointerup") moveEdgePress(event);
  edgePress = null;
  desktop.classList.remove("edge-dragging");
  edgeTab.classList.remove("is-dragging");
  // 概况区仅用于拖动，短按和键盘激活都保持收起状态。
  if (press.active && !cancelled) {
    const rect = desktop.getBoundingClientRect();
    const x = Math.max(0, Math.min(desktop.clientWidth, event.clientX - rect.left - desktop.clientLeft));
    const workHeight = workAreaHeight();
    const y = Math.max(0, Math.min(workHeight, event.clientY - rect.top - desktop.clientTop));
    const distances = [["left", x], ["right", desktop.clientWidth - x], ["top", y], ["bottom", workHeight - y]];
    const side = distances.sort((a, b) => a[1] - b[1])[0][0];
    const offset = isHorizontalEdge(side) ? press.left + (event.clientX - press.x) : press.top + (event.clientY - press.y);
    setEdgePosition(side, offset);
  }
  if (press.active) renderEdge();
  updateDesktopTip();
  refreshAutoCollapse(true);
}
edgeSummary.addEventListener("pointerup", (event) => finishEdgePress(event));
edgeSummary.addEventListener("pointercancel", (event) => finishEdgePress(event, true));
edgeSummary.addEventListener("lostpointercapture", (event) => finishEdgePress(event, true));
edgeSummary.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && edgePress) { finishEdgePress(event, true); return; }
  if (event.key === "Enter" || event.key === " ") { event.preventDefault(); return; }
  const side = { ArrowLeft: "left", ArrowRight: "right", ArrowUp: "top", ArrowDown: "bottom" }[event.key];
  if (!side || edgePress) return;
  event.preventDefault();
  const previous = state.edgePosition;
  if (side === previous.side) return;
  const offset = isHorizontalEdge(side) === isHorizontalEdge(previous.side) ? previous.offset : defaultEdgeOffset(side);
  setEdgePosition(side, offset);
  renderEdge();
});

$("#drag-handle").addEventListener("pointerdown", (event) => {
  if (event.button !== 0 || event.target.closest("button")) return;

  drag = { x: event.clientX, y: event.clientY, left: dock.offsetLeft, top: dock.offsetTop };
  openerBounds = null;
  refreshAutoCollapse(true);
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
  if (event.type === "pointercancel") { positionDock(previous.left, previous.top); refreshAutoCollapse(true); return; }
  const { maxX, maxY } = bounds();
  const dx = event.clientX - previous.x;
  const dy = event.clientY - previous.y;
  if (Math.hypot(dx, dy) < 3) { refreshAutoCollapse(true); return; }
  positionDock(previous.left + dx, previous.top + dy);
  const distances = [
    [Math.abs(dock.offsetLeft), "left", dx <= -3],
    [Math.abs(maxX - dock.offsetLeft), "right", dx >= 3],
    [Math.abs(dock.offsetTop), "top", dy <= -3],
    [Math.abs(maxY - dock.offsetTop), "bottom", dy >= 3],
  ].sort((a, b) => a[0] - b[0]);
  state.panelSide = distances[0][0] <= 20 ? distances[0][1] : null;
  const approached = distances.find(([distance, , toward]) => distance <= 20 && toward);
  if (approached) {
    const side = approached[1];
    state.panelSide = side;
    positionDock(side === "left" ? 0 : side === "right" ? maxX : dock.offsetLeft, side === "top" ? 0 : side === "bottom" ? maxY : dock.offsetTop);
    if (state.showFloatingTab) {
      const offset = isHorizontalEdge(side) ? dock.offsetLeft : dock.offsetTop;
      setEdgePosition(side, offset);
      collapse();
    }
  }
  refreshAutoCollapse(true);
}
$("#drag-handle").addEventListener("pointerup", finishDrag);
$("#drag-handle").addEventListener("pointercancel", finishDrag);
const edgeOpenButton = $("#edge-open-button");
edgeOpenButton.addEventListener("click", () => { state.selected = null; render(); expand(true); });
edgeTab.addEventListener("click", (event) => {
  const deviceButton = event.target.closest("[data-edge-device]");
  if (deviceButton) {
    openDevice(deviceButton.dataset.edgeDevice);
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
edgeTab.addEventListener("pointerdown", (event) => {
  const button = event.target.closest("[data-edge-device]");
  if (event.button !== 0 || !button) return;
  iconSwipe = { pointerId: event.pointerId, id: button.dataset.edgeDevice, x: event.clientX, y: event.clientY, active: false };
  button.setPointerCapture(event.pointerId);
  refreshAutoCollapse(true);
});
function moveDeviceIconSwipe(event) {
  if (!iconSwipe || iconSwipe.pointerId !== event.pointerId) return;
  const dx = event.clientX - iconSwipe.x;
  const dy = event.clientY - iconSwipe.y;
  if (!iconSwipe.active && dx <= -12 && Math.abs(dx) > Math.abs(dy)) {
    iconSwipe.active = true;
  }
}
edgeTab.addEventListener("pointermove", moveDeviceIconSwipe);
function finishDeviceIconSwipe(event) {
  if (!iconSwipe || iconSwipe.pointerId !== event.pointerId) return;
  if (event.type === "pointerup") moveDeviceIconSwipe(event);
  const deviceId = event.type === "pointerup" && iconSwipe.active ? iconSwipe.id : null;
  iconSwipe = null;
  if (deviceId) openDevice(deviceId);
  refreshAutoCollapse(true);
}
edgeTab.addEventListener("pointerup", finishDeviceIconSwipe);
edgeTab.addEventListener("pointercancel", finishDeviceIconSwipe);
edgeTab.addEventListener("lostpointercapture", finishDeviceIconSwipe);
$("#close-panel-button").addEventListener("click", closePanel);
$("#edge-demo-button").addEventListener("click", () => {
  closePanel();
  notify(state.showFloatingTab ? "点击设备图标直达操作，或用“展开”查看全部设备。" : "已隐藏到模拟托盘，点击任务栏中的 ADB Dock 图标重新打开。");
});
$("#settings-button").addEventListener("click", showSettings);
$("#floating-tab-button").addEventListener("click", () => setShowFloatingTab(!state.showFloatingTab));
$("#tray-reopen-button").addEventListener("click", () => {
  expand(true);
  if (!dock.hidden) $("#close-panel-button").focus({ preventScroll: true });
  refreshAutoCollapse(true);
});
$$("[data-scenario]").forEach((button) => button.addEventListener("click", () => changeScenario(button.dataset.scenario)));
$("#dialog-close").addEventListener("click", closeDialog);
dialog.addEventListener("close", () => {
  if (dialogReturnFocus?.isConnected && !dock.hidden) dialogReturnFocus.focus({ preventScroll: true });
  dialogReturnFocus = null;
  refreshAutoCollapse(true);
});
dialog.addEventListener("click", (event) => {
  const rect = dialog.getBoundingClientRect();
  if (event.target === dialog && (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom)) closeDialog();
});
$("#apk-input").addEventListener("change", (event) => {
  filePickerOpen = false;
  refreshAutoCollapse(true);
  const file = event.target.files[0];
  const target = fileTarget;
  fileTarget = null;
  if (!file || !target) {  return; }
  if (!file.name.toLowerCase().endsWith(".apk")) { notify("请选择 .apk 文件。该文件不会上传。");  return; }
  const device = currentDevices().find((item) => item.id === target.deviceId);
  if (!device || target.generation !== state.generation) { notify("设备状态已变化，已取消模拟安装。");  return; }
  runTask(device, "安装", `模拟安装完成 · ${file.name}（未上传、未安装）`);

});
$("#apk-input").addEventListener("cancel", () => { filePickerOpen = false; fileTarget = null; refreshAutoCollapse(true); });
$("#reset-button").addEventListener("click", () => {


  closeDialog();
  state.topmost = true;
  setEdgePosition("right", defaultEdgeOffset("right"));
  resetPosition();
  changeScenario("mixed");
  notify("已重置场景和位置，浮窗与收起设置保持不变。");
});
new ResizeObserver(() => {
  if (!initialized) return;
  if (state.hiddenToTray) return;
  if (state.collapsed) {
    renderEdge();
    return;
  }
  const { panelSide } = state;
  const { maxX, maxY } = bounds();
  positionDock(panelSide === "left" ? 0 : panelSide === "right" ? maxX : dock.offsetLeft, panelSide === "top" ? 0 : panelSide === "bottom" ? maxY : dock.offsetTop);
  openerBounds = null;
  refreshAutoCollapse(true);
}).observe(desktop);
render();
resetPosition();
initialized = true;
footer("设备状态已同步");
