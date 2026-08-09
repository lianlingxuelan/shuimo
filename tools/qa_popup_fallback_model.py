# -*- coding: utf-8 -*-
"""
qa_popup_fallback_model.py —— DamagePopupLayer.TryPlace 降级修复的数值/逻辑对拍模型

本环境无 Unity / 无 dotnet，无法跑 NUnit。本脚本把 DamagePopupLayer 的
Push / FindMergeTarget / AcquireSlotIndex / ApplyBounds / Recycle / ClearAll
按 C# 源码逐行翻译成 Python，然后用 P1_2_HitFeedbackTests.cs 里 Popup_* 用例
的**完全相同的调用序列**驱动它，断言与测试文件里逐字一致的期望值。

它证明的是：**在"投影不可用 ⇒ 走降级分支"这一前提下，被测逻辑会满足断言**。
它不能证明 C# 能编译，也不能证明真机上投影是否真的失败（那要 Unity）。

常量取自 HitFeedbackConfig.cs / HudSkillBar.cs（已 grep 核实）。
"""

import math

# ---- 常量（HitFeedbackConfig.cs，已核实行号）--------------------------------
POPUP_CAPACITY        = 12      # :281
POPUP_MERGE_WINDOW    = 0.15    # :284
POPUP_LIFETIME        = 0.70    # :287
POPUP_SCALE_FROM      = 0.80    # :296
POPUP_HEAVY_FONT_MUL  = 1.30    # :311
POPUP_VIEWPORT_PAD_PX = 24.0    # :341
POPUP_HUD_SAFE_Y      = 120.0   # :351
# HudSkillBar.cs
SKILLBAR_CELL_SIZE    = 76.0    # :31
SKILLBAR_BOTTOM_MARGIN= 34.0    # :37


def clamp(v, lo, hi):
    return lo if v < lo else (hi if v > hi else v)


class Rect:
    """UnityEngine.Rect 的最小模型（xMin/yMin/width/height/center）。"""
    def __init__(self, xmin, ymin, w, h):
        self.xMin, self.yMin, self.width, self.height = xmin, ymin, w, h

    @property
    def center(self):
        return (self.xMin + self.width * 0.5, self.yMin + self.height * 0.5)


class Slot:
    def __init__(self):
        self.Alive = False
        self.TargetId = -1
        self.Age = 0.0
        self.Damage = 0.0
        self.Shown = -2147483648
        self.BasePos = (0.0, 0.0)
        self.RisePx = 0.0
        self.BaseScale = 1.0
        self.ActiveSelf = False   # 对应 slot.Rect.gameObject.activeSelf
        self.Text = ""


class PopupLayer:
    """DamagePopupLayer 的逐行翻译。project_ok 模拟 TryProject 成败。"""

    def __init__(self, canvas_rect, project_ok, cam_present=True):
        self._slots = [Slot() for _ in range(POPUP_CAPACITY)]
        self._cursor = 0
        self._canvasRect = canvas_rect          # None 表示 Build 未跑
        self._project_ok = project_ok
        self._cam = cam_present
        self._frozen = False

    # ---- L773 ApplyBounds(Vector2 pos, float risePx) -> Vector2 -------------
    def ApplyBounds(self, pos, risePx):
        r = self._canvasRect
        pad = POPUP_VIEWPORT_PAD_PX
        maxX = max(pad, r.width - pad)
        maxY = max(pad, r.height - pad - max(0.0, risePx))
        x = clamp(pos[0], pad, maxX)
        y = clamp(pos[1], pad, maxY)
        barTop = SKILLBAR_BOTTOM_MARGIN + SKILLBAR_CELL_SIZE   # 110.0
        if y <= barTop:
            y = POPUP_HUD_SAFE_Y
        y = min(y, maxY)          # P2-03 视口硬约束赢过 HUD 避让
        return (x, y)

    # ---- L676 TryPlace（含本轮修复的降级分支 L710-719）----------------------
    def TryPlace(self, world_pos):
        spawn = (0.0, 0.0)
        risePx = 0.0
        cam = self._cam
        if (cam is None) or (self._canvasRect is None) or (not self._project_ok):
            if self._canvasRect is not None:
                fb = self._canvasRect
                spawn = self.ApplyBounds((fb.center[0], fb.center[1]), 0.0)
            return True, spawn, risePx            # ★ 修复点：旧实现此处 return False
        base_local = (self._canvasRect.width * 0.5, self._canvasRect.height * 0.5)
        spawn = self.ApplyBounds(base_local, risePx)
        return True, spawn, risePx

    # ---- L656 FindMergeTarget ---------------------------------------------
    def FindMergeTarget(self, target_id):
        for s in self._slots:
            if s.Alive and s.TargetId == target_id and s.Age < POPUP_MERGE_WINDOW:
                return s
        return None

    # ---- L623 AcquireSlotIndex --------------------------------------------
    def AcquireSlotIndex(self):
        n = len(self._slots)
        for i in range(n):                      # ① 空闲优先
            k = (self._cursor + i) % n
            if not self._slots[k].Alive:
                return k
        oldest, max_age = 0, -1.0               # ② 池满覆盖 Age 最大
        for i in range(n):
            k = (self._cursor + i) % n
            if self._slots[k].Age > max_age:
                max_age, oldest = self._slots[k].Age, k
        return oldest

    # ---- L536 ApplyText ----------------------------------------------------
    @staticmethod
    def ApplyText(slot):
        d = slot.Damage
        if math.isnan(d) or math.isinf(d):
            d = 0.0
        # Mathf.RoundToInt = banker's rounding (round-half-to-even)，与 Python round 同
        shown = max(0, int(round(d)))
        if shown == slot.Shown:
            return
        slot.Shown = shown
        slot.Text = str(shown)

    # ---- L251 Push ---------------------------------------------------------
    def Push(self, target_id, world_pos, dmg, heavy=False):
        if self._slots is None or dmg <= 0.0:
            return
        if math.isnan(dmg) or math.isinf(dmg):
            return
        merged = self.FindMergeTarget(target_id)          # ① 合并窗口
        if merged is not None:
            merged.Damage += dmg
            if heavy:
                merged.BaseScale = POPUP_HEAVY_FONT_MUL
            merged.Age = 0.0
            self.ApplyText(merged)
            return
        ok, spawn, rise = self.TryPlace(world_pos)        # ② 先投影
        if not ok:
            return
        idx = self.AcquireSlotIndex()                     # ③ 取槽位
        slot = self._slots[idx]
        self._cursor = (idx + 1) % len(self._slots)
        slot.Alive = True
        slot.TargetId = target_id
        slot.Age = 0.0
        slot.Damage = dmg
        slot.Shown = -2147483648
        slot.BasePos = spawn
        slot.RisePx = rise
        slot.BaseScale = POPUP_HEAVY_FONT_MUL if heavy else 1.0
        self.ApplyText(slot)
        slot.ActiveSelf = True

    def FreezeAll(self, frozen):
        self._frozen = frozen

    def ClearAll(self):
        self._frozen = False
        if self._slots is None:
            return
        for s in self._slots:
            s.Alive = False
            s.TargetId = -1
            s.Age = 0.0
            s.Damage = 0.0
            s.ActiveSelf = False

    # ---- 测试辅助：对应 CountActivePopups / FirstActivePopupText -------------
    def count_active(self):
        return sum(1 for s in self._slots if s.ActiveSelf)

    def first_active_text(self):
        for s in self._slots:
            if s.ActiveSelf:
                return s.Text
        return None


# =============================================================================
# 用例驱动
# =============================================================================
RESULTS = []


def check(name, cond, detail=""):
    RESULTS.append((name, bool(cond), detail))


def new_layer(project_ok, canvas=None):
    """project_ok=False 复现 EditMode（投影不可用）；True 复现真机。"""
    if canvas is None:
        canvas = Rect(-960.0, -540.0, 1920.0, 1080.0)   # 设计分辨率画布
    return PopupLayer(canvas, project_ok)


def run_suite(project_ok, tag):
    p = lambda: new_layer(project_ok)

    # Popup_RejectsInvalidDamage —— 期望 0
    L = p()
    L.Push(1, None, 0.0); L.Push(2, None, -5.0)
    L.Push(3, None, float('nan')); L.Push(4, None, float('inf'))
    check(tag + " Popup_RejectsInvalidDamage", L.count_active() == 0,
          "expected 0, got %d" % L.count_active())

    # Popup_Push_ShowsRoundedIntegerText —— 期望 1 / "12"
    L = p(); L.Push(1, None, 12.4)
    check(tag + " Popup_Push_ShowsRoundedIntegerText[count]", L.count_active() == 1,
          "expected 1, got %d" % L.count_active())
    check(tag + " Popup_Push_ShowsRoundedIntegerText[text]", L.first_active_text() == "12",
          "expected '12', got %r" % L.first_active_text())

    # Popup_SameTargetWithinWindow_MergesIntoOneSlot —— 期望 1 / "34"
    L = p(); L.Push(7, None, 20.0); L.Push(7, None, 14.0)
    check(tag + " Popup_SameTargetWithinWindow_MergesIntoOneSlot[count]", L.count_active() == 1,
          "expected 1, got %d" % L.count_active())
    check(tag + " Popup_SameTargetWithinWindow_MergesIntoOneSlot[text]", L.first_active_text() == "34",
          "expected '34', got %r" % L.first_active_text())

    # Popup_DifferentTargets_DoNotMerge —— 期望 2
    L = p(); L.Push(1, None, 10.0); L.Push(2, None, 10.0)
    check(tag + " Popup_DifferentTargets_DoNotMerge", L.count_active() == 2,
          "expected 2, got %d" % L.count_active())

    # Popup_PoolNeverExceedsCapacity_UnderBurst —— 期望 <= 12
    L = p()
    for i in range(POPUP_CAPACITY * 3):
        L.Push(100 + i, None, 5.0 + i)
    check(tag + " Popup_PoolNeverExceedsCapacity_UnderBurst",
          L.count_active() <= POPUP_CAPACITY, "got %d (cap %d)" % (L.count_active(), POPUP_CAPACITY))

    # Popup_FreezeAll_KeepsSlotsAlive_A7 —— 前置 3，冻结后仍 3
    L = p()
    L.Push(1, None, 10.0); L.Push(2, None, 20.0); L.Push(3, None, 30.0)
    pre = L.count_active()
    L.FreezeAll(True)
    check(tag + " Popup_FreezeAll_KeepsSlotsAlive_A7[pre]", pre == 3, "expected 3, got %d" % pre)
    check(tag + " Popup_FreezeAll_KeepsSlotsAlive_A7[post]", L.count_active() == 3,
          "expected 3, got %d" % L.count_active())

    # Popup_ClearAll_LeavesNothingOnScreen_A8 —— 前置 >0，清后 0
    L = p()
    for i in range(5):
        L.Push(200 + i, None, 10.0 + i)
    pre = L.count_active()
    L.ClearAll()
    check(tag + " Popup_ClearAll_LeavesNothingOnScreen_A8[pre>0]", pre > 0, "got %d" % pre)
    check(tag + " Popup_ClearAll_LeavesNothingOnScreen_A8[post=0]", L.count_active() == 0,
          "got %d" % L.count_active())

    # Popup_ClearAll_WhileFrozen_StillAcceptsNewPush
    L = p(); L.Push(1, None, 10.0); L.FreezeAll(True); L.ClearAll()
    mid = L.count_active()
    L.Push(2, None, 15.0)
    check(tag + " Popup_ClearAll_WhileFrozen_StillAcceptsNewPush[cleared]", mid == 0, "got %d" % mid)
    check(tag + " Popup_ClearAll_WhileFrozen_StillAcceptsNewPush[reaccept]", L.count_active() == 1,
          "expected 1, got %d" % L.count_active())

    # Popup_Disable_ClearsEverything（OnDisable -> ClearAll）
    L = p(); L.Push(1, None, 10.0)
    pre = L.count_active(); L.ClearAll()
    check(tag + " Popup_Disable_ClearsEverything[pre]", pre == 1, "expected 1, got %d" % pre)
    check(tag + " Popup_Disable_ClearsEverything[post]", L.count_active() == 0,
          "got %d" % L.count_active())


def run_bounds_safety():
    """降级落点的数值安全性：任意画布尺寸下都必须有限、在 [pad, max] 内、非 NaN。"""
    bad = []
    for w, h in [(1920.0, 1080.0), (1280.0, 720.0), (640.0, 360.0),
                 (200.0, 100.0), (48.0, 48.0), (0.0, 0.0), (1.0, 1.0), (3840.0, 2160.0)]:
        r = Rect(-w * 0.5, -h * 0.5, w, h)
        L = PopupLayer(r, project_ok=False)
        _, spawn, rise = L.TryPlace(None)
        x, y = spawn
        pad = POPUP_VIEWPORT_PAD_PX
        maxX = max(pad, w - pad)
        maxY = max(pad, h - pad)
        ok = (not math.isnan(x) and not math.isnan(y)
              and not math.isinf(x) and not math.isinf(y)
              and pad - 1e-6 <= x <= maxX + 1e-6
              and pad - 1e-6 <= y <= maxY + 1e-6
              and rise == 0.0)
        if not ok:
            bad.append((w, h, x, y, rise))
    check("降级落点数值安全（8 种画布尺寸，无 NaN/Inf、恒在视口内）", not bad, str(bad))

    # _canvasRect == null 的极端分支：spawn 保持 (0,0)，不得抛
    L = PopupLayer(None, project_ok=False)
    ok, spawn, rise = L.TryPlace(None)
    check("_canvasRect==null 降级分支不抛异常且 out 参数已赋值",
          ok is True and spawn == (0.0, 0.0) and rise == 0.0,
          "ok=%s spawn=%s rise=%s" % (ok, spawn, rise))

    # 合并窗口边界：Age 恰好等于窗口值时不得合并（严格 <）
    L = new_layer(False)
    L.Push(7, None, 20.0)
    L._slots[0].Age = POPUP_MERGE_WINDOW
    L.Push(7, None, 14.0)
    check("合并窗口是严格开区间：Age == PopupMergeWindow 时不合并（另起一条）",
          L.count_active() == 2, "got %d" % L.count_active())

    # 窗口内边界：Age 略小于窗口值时必须合并
    L = new_layer(False)
    L.Push(7, None, 20.0)
    L._slots[0].Age = POPUP_MERGE_WINDOW - 1e-4
    L.Push(7, None, 14.0)
    check("合并窗口内（Age = 窗口 - 1e-4）必须合并且累加为 34",
          L.count_active() == 1 and L.first_active_text() == "34",
          "count=%d text=%r" % (L.count_active(), L.first_active_text()))


def main():
    print("=" * 78)
    print("  DamagePopupLayer.TryPlace 降级修复 —— 逻辑对拍模型（无 Unity 环境）")
    print("=" * 78)

    print("\n--- A. EditMode 情形（投影不可用 ⇒ 走本轮新增的降级分支）---")
    run_suite(project_ok=False, tag="[EditMode]")

    print("--- B. 真机情形（投影走通 ⇒ 精确路径，验证修复未改变原行为）---")
    run_suite(project_ok=True, tag="[真机]")

    print("--- C. 降级落点数值安全 + 合并窗口边界 ---")
    run_bounds_safety()

    print()
    npass = sum(1 for _, ok, _ in RESULTS if ok)
    for name, ok, detail in RESULTS:
        flag = "PASS" if ok else "FAIL"
        print("  [%s] %-72s %s" % (flag, name, "" if ok else detail))
    print()
    print("=" * 78)
    print("  汇总: %d / %d 通过" % (npass, len(RESULTS)))
    print("  结果: %s" % ("PASS" if npass == len(RESULTS) else "FAIL"))
    print("=" * 78)
    return 0 if npass == len(RESULTS) else 1


if __name__ == "__main__":
    raise SystemExit(main())
