#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
t1_selfcheck.py —— T1 战斗内核算法对拍脚本（Godot 基线 + T0 内核）

【它为什么存在】
本地环境没有 dotnet，`Assets/Scripts/Systems/Combat/*.cs` 无法编译验证。但
「围攻承伤频率 4.300 次/s」「模型 B 反调 d_eff ≈ hp/65」「BOSS 阶段阈值」这些
东西一旦算错，等到 Unity 里才发现就已经晚了——那时手感已经被错误数值带偏，
再改就是推翻重来。于是把 C# 内核的算法用 Python 一比一重写，直接对拍
Godot 原型基线。两套独立实现得出同一组数字，才说明移植的是「算法」
而不是「一堆看起来很像的代码」。

【与 T0 的关系：不复制，直接 import】
两层闸门（WCoreState）、固定步累加器、PCG32、djb2、难度公式全部**直接引用**
`Assets/Scripts/Core/wcore_selfcheck.py`，而不是在这里再抄一份。理由很直白：
抄一份就等于埋下一个会各自漂移的分身，而 T1-14「端到端锁步」的全部意义，
恰恰在于证明 T1 内核路由进的是**同一个** W-CORE。共用实现，这条断言才成立。

【对拍基线】
    Godot r2_t01: 1 源 1.700 次/s   4 源 4.300 次/s   围攻/单挑 = 2.53x
    game_config.gd: TOUCH_RANGE=44  LEASH=480  STRIKE_DIST=160
                    WINDUP=0.35  DASH=0.25  STRIKE_CD=3.5  DMG_MULT=1.5
    main.gd:        hp_scale=1+0.18(lv-1)  dmg_scale=1+0.14(lv-1)
                    elite HP×2.6 ATK×1.5 EXP×2.0
    BOSS:           阈值 [0.65, 0.30]  P2 速度×1.25 召唤 2/8s
                    P3 攻击×1.40 速度×1.40 冲击波 r=200 /6s

用法:
    python t1_selfcheck.py
退出码 0 = 全过，1 = 有 FAIL。
"""

import math
import os
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

# -----------------------------------------------------------------------------
# 引入 T0 内核（Assets/Scripts/Core/wcore_selfcheck.py）
# 路径：Systems/Combat/Tests → Systems/Combat → Systems → Scripts → Scripts/Core
# -----------------------------------------------------------------------------

_HERE = os.path.dirname(os.path.abspath(__file__))
_CORE_DIR = os.path.abspath(os.path.join(_HERE, "..", "..", "..", "Core"))
if _CORE_DIR not in sys.path:
    sys.path.insert(0, _CORE_DIR)

try:
    import wcore_selfcheck as t0
except ImportError as exc:  # pragma: no cover
    print("[FATAL] 找不到 T0 内核 wcore_selfcheck.py，期望路径: %s" % _CORE_DIR)
    print("        %s" % exc)
    sys.exit(1)

# T0 直接复用的符号（不重写，保证逐位同构）
WCoreState = t0.WCoreState
FixedStepAccumulator = t0.FixedStepAccumulator
PCG32 = t0.PCG32
djb2 = t0.djb2
zone_seed = t0.zone_seed
compute_deff = t0.compute_deff
mitigation_of = t0.mitigation_of

FIXED_STEP = t0.FIXED_STEP          # = 1/60，绝不写 0.01667 字面量
PER_SOURCE_HIT_CD = t0.PER_SOURCE_HIT_CD
GLOBAL_HIT_GAP = t0.GLOBAL_HIT_GAP
DEFF_DIVISOR = t0.DEFF_DIVISOR
DEFF_DIVISOR_MAX = t0.DEFF_DIVISOR_MAX
DEFF_DIVISOR_MIN = t0.DEFF_DIVISOR_MIN
DEF_SOFTCAP_K = t0.DEF_SOFTCAP_K
PCG_DEFAULT_SEED = t0.PCG_DEFAULT_SEED

# U1：模型 B 的 d_eff 靶子分子 —— **玩家**血量上限。
# 与 C# DifficultyBridge.PlayerHpMax / CombatController.playerHpMax 三处对齐，
# 260/65 = 4.0，是每次接触玩家「应该」掉的血。
PLAYER_HP_MAX = 260.0


# =============================================================================
# CombatConfig 的 Python 镜像（逐值对齐 Combat/CombatConfig.cs 与 game_config.gd）
# 改这里 = 改手感，必须同步改 CombatConfig.cs
# =============================================================================

# --- 接触判定 ---
TOUCH_RANGE = 44.0                  # = 22 × HITBOX_SCALE(2)

# --- AI ---
AI_LEASH_DIST = 480.0
AI_STRIKE_DIST = 160.0
AI_CHASE_OFFSET_DEG = 35.0
AI_CHASE_REDIR_CD = 1.2
AI_STRIKE_WINDUP = 0.35
AI_STRIKE_DASH = 0.25
AI_STRIKE_SPEED_MULT = 3.2
AI_STRIKE_DMG_MULT = 1.5
AI_STRIKE_CD = 3.5
AI_PATROL_SPEED_MULT = 0.3
AI_PATROL_ANCHOR_RADIUS = 20.0

# --- BOSS ---
BOSS_PHASE_P2_THRESHOLD = 0.65
BOSS_PHASE_P3_THRESHOLD = 0.30
BOSS_P2_SPEED_MULT = 1.25
BOSS_P2_ATK_MULT = 1.0
BOSS_P2_SUMMON_CD = 8.0
BOSS_P2_SUMMON_N = 2
BOSS_P3_ATK_MULT = 1.40
BOSS_P3_SPEED_MULT = 1.40
BOSS_P3_SHOCK_CD = 6.0
BOSS_P3_SHOCK_RADIUS = 200.0
BOSS_POISE_MULT = 3.0
BOSS_EXP_MULT = 3.0
BOSS_SUMMON_LEVEL_OFFSET = -1

# --- 玩家基准数值 ---
# U1：模型 B 反调的 d_eff 靶子分子。必须与 C# 两处保持同一个数：
#   DifficultyBridge.PlayerHpMax = 260.0f
#   CombatController.playerHpMax = 260.0f
# 改这里而不同步改 C#，对拍会在 T1-13 立刻炸出来——这正是它存在的意义。
PLAYER_HP_MAX = 260.0

# --- 分级缩放（main.gd）---
ENEMY_HP_PER_LEVEL = 0.18
ENEMY_DMG_PER_LEVEL = 0.14
ENEMY_LEVEL_MIN = 1
ENEMY_LEVEL_MAX = 60
ENEMY_LEVEL_JITTER_MIN = -1
ENEMY_LEVEL_JITTER_MAX = 2
ELITE_HP_MULT = 2.6
ELITE_ATK_MULT = 1.5
ELITE_EXP_MULT = 2.0
AFFIX_SWIFT_SPEED_MULT = 1.35
AFFIX_IRONHIDE_ARMOR_ADD = 12.0     # game_config.gd 的 0.12，落地时 ×100
AFFIX_BLAZE_DMG_MULT = 1.25

# --- 霸体 / 击退 ---
POISE_MAX_DEFAULT = 30.0
POISE_REGEN = 20.0
POISE_REGEN_DELAY = 2.0
KNOCKBACK_FRICTION = 1400.0
KNOCKBACK_STOP_SPEED = 1.0
POISE_BREAK_HITSTUN = 0.20
POISE_BREAK_KNOCKBACK_DIST = 48.0

ENEMY_DAMAGE_FLOOR = 1.0
VEC_EPSILON = 0.001

# --- 敌人基础数值（main.gd ENEMY_SPECS + POISE_MAX_BY_KIND）---
#     kind: (hp, speed, damage, armor, exp, poise_max, 显示名)
ENEMY_SPECS = {
    "blood":   (30.0, 60.0,  8.0, 3.0, 8.0, 60.0, "血煞"),
    "witch":   (22.0, 95.0,  6.0, 1.0, 6.0, 24.0, "巫蛊"),
    "sword":   (28.0, 80.0, 10.0, 4.0, 7.0, 34.0, "剑修"),
    "alchemy": (24.0, 70.0,  5.0, 2.0, 6.0, 26.0, "丹修"),
}

# --- 验收基线与容差（设计文档 §7.3）---
FREQ_SOLO = 1.700
FREQ_SWARM = 4.300
SWARM_MULT = 2.53
FREQ_TOL = 0.02
MULT_TOL = 0.03
REL_TOL = 1e-6


# =============================================================================
# Vec2 的 Python 等价实现（对应 Combat/Vec2.cs）
# =============================================================================

class Vec2(object):
    """二维向量。语义逐行对齐 C# 版（含零向量退化行为）。"""

    __slots__ = ("x", "y")

    def __init__(self, x=0.0, y=0.0):
        self.x = float(x)
        self.y = float(y)

    # --- 运算符 ---
    def __add__(self, o):
        return Vec2(self.x + o.x, self.y + o.y)

    def __sub__(self, o):
        return Vec2(self.x - o.x, self.y - o.y)

    def __mul__(self, k):
        return Vec2(self.x * k, self.y * k)

    __rmul__ = __mul__

    def __neg__(self):
        return Vec2(-self.x, -self.y)

    def __repr__(self):
        return "(%.3f, %.3f)" % (self.x, self.y)

    # --- 长度与方向 ---
    def length(self):
        return math.sqrt(self.x * self.x + self.y * self.y)

    def length_squared(self):
        return self.x * self.x + self.y * self.y

    def normalized(self):
        ln = self.length()
        if ln <= VEC_EPSILON:
            return Vec2(0.0, 0.0)
        return Vec2(self.x / ln, self.y / ln)

    def with_length(self, ln):
        if ln <= 0.0:
            return Vec2(0.0, 0.0)
        n = self.normalized()
        return Vec2(n.x * ln, n.y * ln)

    def distance_to(self, o):
        dx = o.x - self.x
        dy = o.y - self.y
        return math.sqrt(dx * dx + dy * dy)

    def distance_squared_to(self, o):
        dx = o.x - self.x
        dy = o.y - self.y
        return dx * dx + dy * dy

    def direction_to(self, o):
        return Vec2(o.x - self.x, o.y - self.y).normalized()

    def dot(self, o):
        return self.x * o.x + self.y * o.y

    def cross(self, o):
        return self.x * o.y - self.y * o.x

    def angle_deg_between(self, o):
        l1 = self.length()
        l2 = o.length()
        if l1 <= VEC_EPSILON or l2 <= VEC_EPSILON:
            return 0.0
        c = self.dot(o) / (l1 * l2)
        c = max(-1.0, min(1.0, c))      # 共线时浮点误差会把 c 推到 1.0000001 → acos NaN
        return math.degrees(math.acos(c))

    def angle(self):
        return math.atan2(self.y, self.x)

    def rotated(self, radians):
        cs = math.cos(radians)
        sn = math.sin(radians)
        return Vec2(self.x * cs - self.y * sn, self.x * sn + self.y * cs)

    def rotated_deg(self, degrees):
        return self.rotated(math.radians(degrees))

    def lerp(self, o, t):
        k = max(0.0, min(1.0, t))
        return Vec2(self.x + (o.x - self.x) * k, self.y + (o.y - self.y) * k)

    def is_zero(self):
        return self.length() <= VEC_EPSILON


VEC_ZERO = Vec2(0.0, 0.0)
VEC_RIGHT = Vec2(1.0, 0.0)


# =============================================================================
# 阵营 / 词缀 / 状态枚举（对应 Combatant.cs、EnemyAI.cs、BossController.cs）
# =============================================================================

FACTION_PLAYER = 0
FACTION_ENEMY = 1
FACTION_COMPANION = 2               # R6 保留位，本次不实现 0.5x

AFFIX_NONE = 0
AFFIX_SWIFT = 1
AFFIX_IRONHIDE = 2
AFFIX_BLAZE = 3
AFFIX_NAME = {0: "none", 1: "swift", 2: "ironhide", 3: "blaze"}

STATE_PATROL = 0
STATE_CHASE = 1
STATE_STRIKE = 2
STATE_NAME = {0: "PATROL", 1: "CHASE", 2: "STRIKE"}

PHASE_IDLE = 0
PHASE_WINDUP = 1
PHASE_DASH = 2

BOSS_P1 = 0
BOSS_P2 = 1
BOSS_P3 = 2


# =============================================================================
# Combatant 的 Python 等价实现（对应 Combat/Combatant.cs）
# =============================================================================

class Combatant(object):
    """战斗实体。玩家持 WCoreState 走除法减伤，敌人走减法承伤 + 霸体。"""

    def __init__(self):
        self.id = 0
        self.faction = FACTION_ENEMY
        self.kind = "witch"
        self.display_name = ""
        self.level = 1
        self.is_boss = False
        self.is_elite = False
        self.affix = AFFIX_NONE
        self.boss_id = ""
        self.exp_value = 0.0

        self.position = Vec2()
        self.velocity = Vec2()
        self.knockback_vel = Vec2()

        self._hp = 0.0
        self._hp_max = 1.0
        self.wcore = None
        self.ai = None

        self.armor = 0.0
        self.break_def = 0.0
        self.poise = POISE_MAX_DEFAULT
        self.poise_max = POISE_MAX_DEFAULT
        self.poise_regen_timer = 0.0
        self.hit_stun_timer = 0.0

        self.atk = 0.0
        self.contact_damage = 0.0
        self._move_speed = 70.0

    # --- hp / hp_max 对玩家代理到 wcore，杜绝"两份血量各扣各的" ---
    @property
    def hp(self):
        return self.wcore.hp if self.wcore is not None else self._hp

    @hp.setter
    def hp(self, v):
        if self.wcore is not None:
            self.wcore.hp = v
        else:
            self._hp = v

    @property
    def hp_max(self):
        return self.wcore.hp_max if self.wcore is not None else self._hp_max

    @hp_max.setter
    def hp_max(self, v):
        if self.wcore is not None:
            self.wcore.hp_max = v
        else:
            self._hp_max = v

    @property
    def move_speed(self):
        return self.ai.speed_base if self.ai is not None else self._move_speed

    @move_speed.setter
    def move_speed(self, v):
        self._move_speed = v
        if self.ai is not None:
            self.ai.speed_base = v

    @property
    def is_alive(self):
        return self.hp > 0.0

    @property
    def is_enemy(self):
        return self.faction == FACTION_ENEMY

    def hp_ratio(self):
        mx = self.hp_max if self.hp_max > 0.0 else 1.0
        return max(0.0, min(1.0, self.hp / mx))

    # --- 敌人侧减法承伤 ---
    def apply_enemy_damage(self, raw, attacker_break_def=0.0):
        """real = max(1, raw − max(0, armor − break_def))"""
        eff_armor = max(0.0, self.armor - attacker_break_def)
        real = max(ENEMY_DAMAGE_FLOOR, raw - eff_armor)
        self.hp = max(0.0, self.hp - real)
        return real

    # --- 霸体 / 硬直 / 击退 ---
    def damage_poise(self, amount):
        """返回 True 表示本次破韧。破韧立刻回满，不停在 0（否则霸体形同虚设）。"""
        self.poise_regen_timer = POISE_REGEN_DELAY
        self.poise -= max(0.0, amount)
        if self.poise <= 0.0:
            self.poise = self.poise_max
            return True
        return False

    def tick_poise(self, dt):
        if self.poise_regen_timer > 0.0:
            self.poise_regen_timer = max(0.0, self.poise_regen_timer - dt)
            return
        if self.poise < self.poise_max:
            self.poise = min(self.poise_max, self.poise + POISE_REGEN * dt)

    def apply_hit_stun(self, seconds):
        if seconds > 0.0:
            self.hit_stun_timer = max(self.hit_stun_timer, seconds)

    def tick_hit_stun(self, dt):
        if self.hit_stun_timer > 0.0:
            self.hit_stun_timer = max(0.0, self.hit_stun_timer - dt)

    def take_knockback(self, impulse):
        """R2：只写速度脉冲，不做墙体解算（内核按开阔地模拟）。"""
        self.knockback_vel = self.knockback_vel + impulse

    def integrate_knockback(self, dt):
        if self.knockback_vel.is_zero():
            self.knockback_vel = Vec2()
            return False
        self.position = self.position + self.knockback_vel * dt
        sp = self.knockback_vel.length() - KNOCKBACK_FRICTION * dt
        if sp <= KNOCKBACK_STOP_SPEED:
            self.knockback_vel = Vec2()
        else:
            self.knockback_vel = self.knockback_vel.normalized() * sp
        return True

    def full_restore(self):
        if self.wcore is not None:
            self.wcore.reset()
            self.wcore.hp = self.wcore.hp_max
        else:
            self.hp = self.hp_max
        self.poise = self.poise_max
        self.poise_regen_timer = 0.0
        self.hit_stun_timer = 0.0
        self.knockback_vel = Vec2()
        self.velocity = Vec2()

    def __repr__(self):
        return "[#%d %s hp=%.1f/%.1f %s]" % (
            self.id, self.display_name or self.kind, self.hp, self.hp_max, self.position)


def knockback_impulse(direction, dist):
    """v0 = sqrt(2 · KNOCKBACK_FRICTION · dist)；方向退化时兜底为 +X。"""
    if dist <= 0.0:
        return Vec2()
    d = direction if not direction.is_zero() else VEC_RIGHT
    v0 = math.sqrt(2.0 * KNOCKBACK_FRICTION * dist)
    return d.normalized() * v0


def create_player(cid, hp_max, pos):
    core = WCoreState()
    core.hp_max = hp_max
    core.hp = hp_max
    core.dodge_enabled = False       # R3：对拍默认关闭
    c = Combatant()
    c.id = cid
    c.faction = FACTION_PLAYER
    c.wcore = core
    c.position = pos
    c.kind = "player"
    c.display_name = "玩家"
    return c


def create_enemy(cid, pos, hp_max, contact_damage, speed):
    c = Combatant()
    c.id = cid
    c.faction = FACTION_ENEMY
    c.position = pos
    c.hp_max = hp_max
    c.hp = hp_max
    c.contact_damage = contact_damage
    c.poise = POISE_MAX_DEFAULT
    c.poise_max = POISE_MAX_DEFAULT
    ai = EnemyAI(c)
    ai.speed_base = speed
    c.ai = ai
    return c


# =============================================================================
# EnemyAI 的 Python 等价实现（对应 Combat/EnemyAI.cs）
# =============================================================================

class EnemyAI(object):
    """三态 FSM：PATROL / CHASE / STRIKE，默认 CHASE（对齐 enemy.gd:40）。"""

    def __init__(self, owner):
        self.owner = owner
        self.state = STATE_CHASE
        self.strike_cd = 0.0
        self.strike_windup = AI_STRIKE_WINDUP
        self.strike_dash = AI_STRIKE_DASH
        self.strike_phase = PHASE_IDLE
        self.strike_timer = 0.0
        self.strike_dir = Vec2()
        self.redir_cd = 0.0
        self.chase_offset_deg = 0.0
        self.speed_base = 70.0
        # C# 侧 Vec2 是 struct（赋值即拷贝）。这里必须显式复制，否则 spawn_pos 会和
        # owner.position 变成同一个对象，敌人一走锚点跟着走，PATROL 永远回不了家。
        self.spawn_pos = (Vec2(owner.position.x, owner.position.y)
                          if owner is not None else Vec2())
        self.rng = None
        self._dashing_this_step = False

        # 本步意图快照
        self.cmd_state = self.state
        self.cmd_velocity = Vec2()
        self.cmd_damage_mult = 1.0
        self.cmd_dashing = False
        self.cmd_strike_started = False
        self.cmd_strike_ended = False

    @property
    def effective_speed(self):
        return self.speed_base

    @property
    def current_damage_mult(self):
        # _dashing_this_step 是「本步在不在突进」的唯一真源，不要再看 strike_phase：
        #   D-2 迟一步：突进最后一步已把相位重置为 IDLE，却仍在以 3.2 倍速位移；
        #   D-3 早一步：蓄力最后一步已把相位翻成 DASH，但返回的是零速度（还在站桩）。
        # 两头都错拍，只有"本步真的产出了突进速度"这个事实是准的。
        return AI_STRIKE_DMG_MULT if self._dashing_this_step else 1.0

    def update(self, dt, target_pos, target_vel=None):
        self.cmd_state = self.state
        self.cmd_velocity = Vec2()
        self.cmd_damage_mult = 1.0
        self.cmd_dashing = False
        self.cmd_strike_started = False
        self.cmd_strike_ended = False
        self._dashing_this_step = False

        if self.owner is None:
            return

        self._tick_timers(dt)
        self._select_state(target_pos)

        # 硬直中：不追击不出手，但击退位移由 Encounter 照常推进
        if self.owner.hit_stun_timer > 0.0:
            self.cmd_state = self.state
            self.cmd_velocity = Vec2()
            self.cmd_damage_mult = self.current_damage_mult
            self.owner.velocity = Vec2()
            return

        vel = self._compute_movement(dt, target_pos)
        self.owner.velocity = vel
        self.cmd_state = self.state
        self.cmd_velocity = vel
        self.cmd_dashing = self._dashing_this_step
        self.cmd_damage_mult = self.current_damage_mult

    def _tick_timers(self, dt):
        if self.strike_cd > 0.0:
            self.strike_cd = max(0.0, self.strike_cd - dt)
        if self.redir_cd > 0.0:
            self.redir_cd = max(0.0, self.redir_cd - dt)

    def _select_state(self, target_pos):
        """脱战 > STRIKE 冷却 > 进入 STRIKE 距离 > 否则 CHASE（enemy.gd:_tick_ai）。"""
        dist = self.owner.position.distance_to(target_pos)
        if dist > AI_LEASH_DIST:
            self.state = STATE_PATROL
            return
        if self.strike_cd > 0.0:
            self.state = STATE_CHASE
            return
        self.state = STATE_STRIKE if dist <= AI_STRIKE_DIST else STATE_CHASE

    def _compute_movement(self, dt, target_pos):
        if self.state == STATE_PATROL:
            return self._compute_patrol()
        if self.state == STATE_STRIKE:
            return self._compute_strike(dt, target_pos)
        return self._compute_chase(target_pos)

    def _compute_patrol(self):
        if self.owner.position.distance_to(self.spawn_pos) < AI_PATROL_ANCHOR_RADIUS:
            return Vec2()
        d = self.owner.position.direction_to(self.spawn_pos)
        return d * (self.effective_speed * AI_PATROL_SPEED_MULT)

    def _compute_chase(self, target_pos):
        # D-1：Godot 每帧重掷偏移角（计时器实为空转），此处按其原意只在到期时重掷。
        if self.redir_cd <= 0.0:
            self.redir_cd = AI_CHASE_REDIR_CD
            self.chase_offset_deg = (
                self.rng.next_range(-AI_CHASE_OFFSET_DEG, AI_CHASE_OFFSET_DEG)
                if self.rng is not None else 0.0)
        d = self.owner.position.direction_to(target_pos)
        if d.is_zero():
            return Vec2()
        return d.rotated_deg(self.chase_offset_deg) * self.effective_speed

    def _compute_strike(self, dt, target_pos):
        if self.strike_phase == PHASE_IDLE:
            # 进入蓄力这一步**只设定计时器、不递减**（对齐 enemy.gd:_process_strike）
            self.strike_phase = PHASE_WINDUP
            self.strike_timer = self.strike_windup
            self.strike_dir = self.owner.position.direction_to(target_pos)
            if self.strike_dir.is_zero():
                self.strike_dir = Vec2(VEC_RIGHT.x, VEC_RIGHT.y)
            self.cmd_strike_started = True
            return Vec2()

        if self.strike_phase == PHASE_WINDUP:
            self.strike_timer -= dt
            if self.strike_timer <= 0.0:
                self.strike_phase = PHASE_DASH
                self.strike_timer = self.strike_dash
            return Vec2()

        if self.strike_phase == PHASE_DASH:
            self.strike_timer -= dt
            self._dashing_this_step = True
            dash_vel = self.strike_dir * (self.effective_speed * AI_STRIKE_SPEED_MULT)
            if self.strike_timer <= 0.0:
                self.strike_phase = PHASE_IDLE
                self.strike_cd = AI_STRIKE_CD
                self.state = STATE_CHASE
                self.cmd_strike_ended = True
            return dash_vel

        self.strike_phase = PHASE_IDLE
        return Vec2()

    def reset(self):
        self.state = STATE_CHASE
        self.strike_cd = 0.0
        self.strike_phase = PHASE_IDLE
        self.strike_timer = 0.0
        self.strike_dir = Vec2()
        self.redir_cd = 0.0
        self.chase_offset_deg = 0.0
        self._dashing_this_step = False


# =============================================================================
# BossController 的 Python 等价实现（对应 Combat/BossController.cs）
# =============================================================================

class BossController(EnemyAI):
    """三阶段控制器。阶段只降不升，每级只抛一次 on_boss_phase。"""

    def __init__(self, owner):
        EnemyAI.__init__(self, owner)
        self.phase = BOSS_P1
        self.summon_cd = 0.0
        self.shock_cd = 0.0
        self.atk_mult = 1.0
        self.speed_mult = 1.0
        self.events = None
        self.spawn = None
        self.pending_spawns = []
        self.shockwave_pending = False
        self.shockwave_center = Vec2()
        self.shockwave_radius = 0.0

    @property
    def effective_speed(self):
        return self.speed_base * self.speed_mult

    @property
    def current_damage_mult(self):
        return EnemyAI.current_damage_mult.fget(self) * self.atk_mult

    def update(self, dt, target_pos, target_vel=None):
        self._check_phase_transition()
        EnemyAI.update(self, dt, target_pos, target_vel)
        if self.phase >= BOSS_P2:
            self._run_p2(dt)
        if self.phase >= BOSS_P3:
            self._run_p3(dt)

    def _check_phase_transition(self):
        if self.owner is None:
            return
        ratio = self.owner.hp_ratio()
        if ratio <= BOSS_PHASE_P3_THRESHOLD:
            target = BOSS_P3
        elif ratio <= BOSS_PHASE_P2_THRESHOLD:
            target = BOSS_P2
        else:
            target = BOSS_P1

        if target <= self.phase:
            return                    # 只降不升

        self.phase = target
        self._apply_phase_multipliers()
        if self.events is not None:
            self.events.on_boss_phase(self.phase)

    def _apply_phase_multipliers(self):
        if self.phase == BOSS_P2:
            self.speed_mult = BOSS_P2_SPEED_MULT
            self.atk_mult = BOSS_P2_ATK_MULT
            self.summon_cd = BOSS_P2_SUMMON_CD
        elif self.phase == BOSS_P3:
            self.speed_mult = BOSS_P3_SPEED_MULT
            self.atk_mult = BOSS_P3_ATK_MULT
            # P1→P3 连跳时 summon_cd 还没初始化过，补上，否则进场瞬间就放一次
            if self.summon_cd <= 0.0:
                self.summon_cd = BOSS_P2_SUMMON_CD
            self.shock_cd = BOSS_P3_SHOCK_CD
        else:
            self.speed_mult = 1.0
            self.atk_mult = 1.0

    def _run_p2(self, dt):
        self.summon_cd -= dt
        if self.summon_cd > 0.0:
            return
        # D-4：进位式复位（保留残差），否则每周期慢一步且逐周期累加。
        self.summon_cd += BOSS_P2_SUMMON_CD
        if self.summon_cd <= 0.0:
            self.summon_cd = BOSS_P2_SUMMON_CD
        n = BOSS_P2_SUMMON_N
        if self.spawn is not None and self.owner is not None:
            for i in range(n):
                lv = max(ENEMY_LEVEL_MIN, self.owner.level + BOSS_SUMMON_LEVEL_OFFSET)
                deg = 360.0 * i / max(1, n)
                pos = self.owner.position + VEC_RIGHT.rotated_deg(deg) * AI_STRIKE_DIST
                minion = self.spawn({"summoner": self.owner, "index": i, "count": n,
                                     "level": lv, "position": pos})
                if minion is not None:
                    self.pending_spawns.append(minion)
        if self.events is not None:
            self.events.on_summon(n)

    def _run_p3(self, dt):
        self.shock_cd -= dt
        if self.shock_cd > 0.0:
            return
        # D-4：同 _run_p2，进位式复位。
        self.shock_cd += BOSS_P3_SHOCK_CD
        if self.shock_cd <= 0.0:
            self.shock_cd = BOSS_P3_SHOCK_CD
        self.shockwave_pending = True
        self.shockwave_center = self.owner.position if self.owner is not None else Vec2()
        self.shockwave_radius = BOSS_P3_SHOCK_RADIUS
        if self.events is not None:
            self.events.on_shockwave(self.shockwave_radius, self.shockwave_center)

    def consume_shockwave(self):
        if not self.shockwave_pending:
            return None
        self.shockwave_pending = False
        return (self.shockwave_center, self.shockwave_radius)

    def reset(self):
        EnemyAI.reset(self)
        self.phase = BOSS_P1
        self.summon_cd = 0.0
        self.shock_cd = 0.0
        self.atk_mult = 1.0
        self.speed_mult = 1.0
        self.shockwave_pending = False
        self.pending_spawns = []


# =============================================================================
# DamageResolver 的 Python 等价实现（对应 Combat/DamageResolver.cs）
# =============================================================================

class NullEvents(object):
    """空事件出口，避免热路径上到处判 None。"""

    def on_hit(self, attacker, defender, dmg, applied):
        pass

    def on_enemy_death(self, e):
        pass

    def on_boss_phase(self, phase):
        pass

    def on_summon(self, n):
        pass

    def on_shockwave(self, radius, center):
        pass

    def on_knockback(self, e, impulse):
        pass


NULL_EVENTS = NullEvents()


def resolve_contact(attacker, defender, rng_range, ev):
    """接触伤害（敌人 → 玩家）。距离判定用纯几何，不依赖任何碰撞体。"""
    if attacker is None or defender is None:
        return False
    if not attacker.is_alive or not defender.is_alive:
        return False
    if attacker.position.distance_to(defender.position) > rng_range:
        return False
    mult = attacker.ai.current_damage_mult if attacker.ai is not None else 1.0
    return apply_to_player(defender, attacker.id, attacker.contact_damage * mult, attacker, ev)


def resolve_shockwave(source, defender, center, radius, ev):
    """冲击波 AoE（BOSS P3）。玩家仍受两层闸门约束。"""
    if source is None or defender is None or not defender.is_alive:
        return False
    if center.distance_to(defender.position) > radius:
        return False
    mult = source.ai.current_damage_mult if source.ai is not None else 1.0
    return apply_to_player(defender, source.id, source.contact_damage * mult, source, ev)


def resolve_player_attack(player, target, raw, ev):
    """R1 钩子：玩家攻击敌人。具体技能公式属于 T2，raw 由上层算好传入。"""
    if player is None or target is None or not target.is_alive:
        return 0.0
    return apply_to_enemy(target, raw, player, ev)


def apply_to_enemy(target, raw, attacker, ev):
    """减法承伤 → 扣韧性 → 破韧则硬直 + 击退 → 死亡事件。"""
    evt = ev if ev is not None else NULL_EVENTS
    break_def = attacker.break_def if attacker is not None else 0.0
    real = target.apply_enemy_damage(raw, break_def)
    evt.on_hit(attacker, target, real, True)

    if target.damage_poise(real):
        target.apply_hit_stun(POISE_BREAK_HITSTUN)
        d = attacker.position.direction_to(target.position) if attacker is not None else VEC_RIGHT
        imp = knockback_impulse(d, POISE_BREAK_KNOCKBACK_DIST)
        target.take_knockback(imp)
        evt.on_knockback(target, imp)

    if not target.is_alive:
        evt.on_enemy_death(target)
    return real


def apply_to_player(target, src_id, raw, attacker, ev):
    """玩家侧：全部走 WCoreState.take_damage_from，由它裁决两层闸门。"""
    evt = ev if ev is not None else NULL_EVENTS
    core = target.wcore
    if core is None:
        fallback = target.apply_enemy_damage(raw, 0.0)
        evt.on_hit(attacker, target, fallback, True)
        return True
    hp_before = core.hp
    applied = core.take_damage_from(src_id, raw)
    dealt = (hp_before - core.hp) if applied else 0.0
    evt.on_hit(attacker, target, dealt, applied)
    return applied


def preview_subtractive(raw, armor, break_def):
    return max(ENEMY_DAMAGE_FLOOR, raw - max(0.0, armor - break_def))


# =============================================================================
# DifficultyBridge 的 Python 等价实现（对应 Combat/DifficultyBridge.cs）
# =============================================================================

class ZoneEnemiesCfg(object):
    """zones.json 的 enemies 段。"""

    def __init__(self, count=6, kinds=None, hp_mult=1.0, atk_mult=1.0,
                 armor_add=0.0, elite_rate=0.0, affix_rate=0.0):
        self.count = count
        self.kinds = kinds if kinds is not None else ["witch"]
        self.hp_mult = hp_mult
        self.atk_mult = atk_mult
        self.armor_add = armor_add
        self.elite_rate = elite_rate
        self.affix_rate = affix_rate


class ZoneBossCfg(object):
    """zones.json 的 boss 段。"""

    def __init__(self, boss_id="boss_zhuxiaowang", display_name="逐霄王", kind="witch",
                 level_offset=2, hp_mult=10.0, atk_mult=1.6):
        self.boss_id = boss_id
        self.display_name = display_name
        self.kind = kind
        self.level_offset = level_offset
        self.hp_mult = hp_mult
        self.atk_mult = atk_mult


def base_stats_of(kind):
    """按 kind 取基础数值副本。未知 kind 回落 witch。"""
    spec = ENEMY_SPECS.get(kind) or ENEMY_SPECS["witch"]
    hp, speed, dmg, armor, exp, poise, name = spec
    return {"kind": kind if kind in ENEMY_SPECS else "witch", "display_name": name,
            "hp": hp, "speed": speed, "damage": dmg, "armor": armor,
            "exp": exp, "poise_max": poise}


def hp_scale(level):
    return 1.0 + ENEMY_HP_PER_LEVEL * (level - 1)


def dmg_scale(level):
    return 1.0 + ENEMY_DMG_PER_LEVEL * (level - 1)


def clamp_level(level):
    return max(ENEMY_LEVEL_MIN, min(ENEMY_LEVEL_MAX, level))


def solve_effective_atk_mult(hp_max, enemy_base_atk, player_def):
    """
    模型 B 反调：atk_mult 只是种子，真正生效的倍率由 d_eff 靶子反解。
        目标 d_eff = player_hp / 65        ← U1：分子是**玩家**血上限
        实际 d_eff = enemy_base_atk · atk_mult_eff · keep
        keep       = 1 − def/(def+100)

    hp_max 形参保留旧名以免大改调用点，但语义已定为「参考血量上限（玩家）」，
    调用方必须传 bridge.player_hp_max。传敌人 hp_max 会让 d_eff 掉到 1 以下，
    被 DAMAGE_FLOOR=1 顶平，玩家单挑 150+ 秒不死。
    """
    if enemy_base_atk <= 0.0:
        return 0.0
    keep = 1.0 - mitigation_of(player_def, DEF_SOFTCAP_K)
    if keep <= 0.0:
        return 0.0
    return compute_deff(hp_max) / (enemy_base_atk * keep)


def roll_elite(rng, elite_rate):
    """用 next_float 而非"概率捷径"：p≤0 时也必须消耗随机流，否则同种子会错位。"""
    if rng is None:
        return False
    return rng.next_float() < elite_rate


def roll_affix(rng, affix_rate):
    if rng is None:
        return AFFIX_NONE
    if not (rng.next_float() < affix_rate):
        return AFFIX_NONE
    pick = rng.next_uint() % 3
    return {0: AFFIX_SWIFT, 1: AFFIX_IRONHIDE}.get(pick, AFFIX_BLAZE)


def apply_affix(c, affix):
    if c is None or affix == AFFIX_NONE:
        return
    c.affix = affix
    if affix == AFFIX_SWIFT:
        c.move_speed = c.move_speed * AFFIX_SWIFT_SPEED_MULT
    elif affix == AFFIX_IRONHIDE:
        c.armor = c.armor + AFFIX_IRONHIDE_ARMOR_ADD
    elif affix == AFFIX_BLAZE:
        c.contact_damage = c.contact_damage * AFFIX_BLAZE_DMG_MULT


def pick_kind(cfg, rng):
    if cfg is None or not cfg.kinds:
        return "witch"
    if rng is None:
        return cfg.kinds[0]
    return cfg.kinds[rng.next_uint() % len(cfg.kinds)]


def roll_level(base_level, rng):
    jitter = rng.next_range_int(ENEMY_LEVEL_JITTER_MIN, ENEMY_LEVEL_JITTER_MAX) if rng else 0
    return clamp_level(base_level + jitter)


class DifficultyBridge(object):
    """把 zones.json 配置 + 等级 + 随机流变成一只可战斗的 Combatant。"""

    def __init__(self, player_def=0.0, player_hp_max=PLAYER_HP_MAX):
        self.player_def = player_def
        # U1：模型 B 的 d_eff 靶子分子 —— 玩家血上限，与 C# DifficultyBridge.PlayerHpMax 对齐。
        self.player_hp_max = player_hp_max
        self.next_id = 1

    def alloc_id(self):
        i = self.next_id
        self.next_id += 1
        return i

    def build_enemy(self, cfg, base, level, rng):
        lv = clamp_level(level)
        hs = hp_scale(lv)
        ds = dmg_scale(lv)

        zone_hp_mult = cfg.hp_mult if (cfg is not None and cfg.hp_mult > 0.0) else 1.0
        armor_add = cfg.armor_add if cfg is not None else 0.0
        elite_rate = cfg.elite_rate if cfg is not None else 0.0
        affix_rate = cfg.affix_rate if cfg is not None else 0.0

        # ① normal 数值
        hp_max = base["hp"] * zone_hp_mult * hs
        # ② 模型 B 反调（U1：靶子分子是**玩家**血上限，不是这只怪自己的 hp_max）
        base_atk_scaled = base["damage"] * ds
        atk_mult_eff = solve_effective_atk_mult(self.player_hp_max, base_atk_scaled, self.player_def)
        contact = base_atk_scaled * atk_mult_eff

        c = create_enemy(self.alloc_id(), Vec2(), hp_max, contact, base["speed"])
        c.kind = base["kind"]
        c.display_name = base["display_name"]
        c.level = lv
        c.armor = base["armor"] + armor_add
        c.exp_value = base["exp"]
        c.poise_max = base["poise_max"]
        c.poise = base["poise_max"]
        c.ai.rng = rng

        # ③ 精英（顺序必须与 Godot spawn_wave 一致）
        if roll_elite(rng, elite_rate):
            c.is_elite = True
            c.hp_max = c.hp_max * ELITE_HP_MULT
            c.hp = c.hp_max
            c.contact_damage = c.contact_damage * ELITE_ATK_MULT
            c.exp_value = c.exp_value * ELITE_EXP_MULT

        # ④ 词缀
        apply_affix(c, roll_affix(rng, affix_rate))
        return c

    def build_enemy_from_cfg(self, cfg, level, rng):
        return self.build_enemy(cfg, base_stats_of(pick_kind(cfg, rng)), level, rng)

    def build_boss(self, cfg, zone_enemies, base_level, rng):
        kind = cfg.kind if cfg is not None else "blood"
        base = base_stats_of(kind)
        lv = clamp_level(base_level + (cfg.level_offset if cfg is not None else 3))
        hs = hp_scale(lv)
        ds = dmg_scale(lv)

        zone_hp_mult = zone_enemies.hp_mult if (zone_enemies is not None and zone_enemies.hp_mult > 0.0) else 1.0
        boss_hp_mult = cfg.hp_mult if (cfg is not None and cfg.hp_mult > 0.0) else 10.0
        boss_atk_mult = cfg.atk_mult if (cfg is not None and cfg.atk_mult > 0.0) else 1.6

        hp_max = base["hp"] * zone_hp_mult * hs * boss_hp_mult
        # U1：反调靶子与杂兵同口径取**玩家**血上限，再乘 boss.atk_mult 作为显式加成；
        # 直接拿 BOSS 的巨额 hp_max 反调会把 d_eff 放大 10 倍。
        base_atk_scaled = base["damage"] * ds
        atk_mult_eff = solve_effective_atk_mult(self.player_hp_max, base_atk_scaled, self.player_def)
        contact = base_atk_scaled * atk_mult_eff * boss_atk_mult

        c = Combatant()
        c.id = self.alloc_id()
        c.faction = FACTION_ENEMY
        c.kind = kind
        c.display_name = cfg.display_name if cfg is not None else "首领"
        c.boss_id = cfg.boss_id if cfg is not None else ""
        c.level = lv
        c.is_boss = True
        c.hp_max = hp_max
        c.hp = hp_max
        c.armor = base["armor"]
        c.contact_damage = contact
        c.exp_value = base["exp"] * BOSS_EXP_MULT
        c.poise_max = base["poise_max"] * BOSS_POISE_MULT
        c.poise = c.poise_max

        boss = BossController(c)
        boss.speed_base = base["speed"]
        boss.rng = rng
        c.ai = boss
        return c

    def is_deff_in_target(self, enemy):
        # U1：窗口分母是**玩家**血上限 —— 这条校验回答「玩家会不会死得太快/太慢」，
        # 与敌人自己有多少血无关。
        keep = 1.0 - mitigation_of(self.player_def, DEF_SOFTCAP_K)
        deff = enemy.contact_damage * keep
        return (self.player_hp_max / DEFF_DIVISOR_MAX) <= deff <= (self.player_hp_max / DEFF_DIVISOR_MIN)


# =============================================================================
# Encounter + CombatScheduler 的 Python 等价实现
# =============================================================================

class Encounter(object):
    """一场战斗的世界状态。单步顺序与 C# 版严格一致。"""

    def __init__(self):
        self.combatants = []
        self.player = None
        self.rng = PCG32()
        self.events = NULL_EVENTS
        self.bridge = DifficultyBridge()
        self.touch_range = TOUCH_RANGE
        self.elapsed_time = 0.0
        self.step_count = 0
        self.max_enemies = 32
        self._pending_add = []

    def set_player(self, p):
        self.player = p
        if p is None:
            return
        if p.wcore is not None:
            p.wcore.reset()
        if p not in self.combatants:
            self.combatants.append(p)

    def add(self, c):
        if c is None or c in self.combatants:
            return
        if c.ai is not None:
            if c.ai.rng is None:
                c.ai.rng = self.rng
            if c.ai.spawn_pos.is_zero():
                c.ai.spawn_pos = c.position
            if isinstance(c.ai, BossController) and c.ai.events is None:
                c.ai.events = self.events
        self.combatants.append(c)

    def alive_enemies(self):
        return [c for c in self.combatants if c.is_enemy and c.is_alive]

    @property
    def alive_enemy_count(self):
        return len(self.alive_enemies())

    def step_fixed(self, dt):
        """① 闸门 tick ② 玩家位移 ③ 敌人 AI+位移 ④ BOSS 技能 ⑤ 接触 ⑥ 收尸"""
        if dt <= 0.0:
            return
        self.step_count += 1
        self.elapsed_time += dt

        # ① 玩家闸门（必须先于任何结算，与 T0 simulate_lockstep 同构）
        if self.player is not None and self.player.wcore is not None:
            self.player.wcore.tick(dt)

        # ② 玩家位移
        if self.player is not None and self.player.is_alive:
            self.player.tick_hit_stun(dt)
            self.player.position = self.player.position + self.player.velocity * dt
            self.player.integrate_knockback(dt)

        target_pos = self.player.position if self.player is not None else Vec2()
        target_vel = self.player.velocity if self.player is not None else Vec2()

        # ③ 敌人
        for e in list(self.combatants):
            if not e.is_enemy or not e.is_alive:
                continue
            e.tick_hit_stun(dt)
            e.tick_poise(dt)
            if e.ai is not None:
                e.ai.update(dt, target_pos, target_vel)
            if e.hit_stun_timer <= 0.0:
                e.position = e.position + e.velocity * dt
            e.integrate_knockback(dt)

        # ④ BOSS 技能收编
        for e in list(self.combatants):
            if not isinstance(e.ai, BossController):
                continue
            boss = e.ai
            if boss.pending_spawns:
                room = self.max_enemies - (self.alive_enemy_count + len(self._pending_add))
                for m in boss.pending_spawns:
                    if room <= 0:
                        break
                    if m is not None:
                        self._pending_add.append(m)
                        room -= 1
                boss.pending_spawns = []
            wave = boss.consume_shockwave()
            if wave is not None and self.player is not None and self.player.is_alive:
                resolve_shockwave(e, self.player, wave[0], wave[1], self.events)

        # ⑤ 接触伤害（按列表顺序 —— 顺序固定是锁步可复现的关键）
        if self.player is not None and self.player.is_alive:
            for e in list(self.combatants):
                if not e.is_enemy or not e.is_alive:
                    continue
                resolve_contact(e, self.player, self.touch_range, self.events)

        # ⑥ 入列 + 收尸
        if self._pending_add:
            for m in self._pending_add:
                self.add(m)
            self._pending_add = []
        self.remove_dead()

    def remove_dead(self):
        dead = [c for c in self.combatants if c.is_enemy and not c.is_alive]
        for d in dead:
            self.combatants.remove(d)
        return len(dead)

    def spawn_minion(self, cfg, req):
        if self.alive_enemy_count + len(self._pending_add) >= self.max_enemies:
            return None
        m = self.bridge.build_enemy_from_cfg(cfg, req["level"], self.rng)
        m.position = req["position"]
        if m.ai is not None:
            m.ai.spawn_pos = req["position"]
            m.ai.rng = self.rng
        return m


class CombatScheduler(object):
    """把任意帧率切成整数个 1/60 逻辑步。绝不出现 0.01667 字面量。"""

    def __init__(self, encounter=None):
        self.accumulator = FixedStepAccumulator()
        self.encounter = encounter
        self.paused = False
        self.total_steps = 0
        self.simulated_time = 0.0
        self.stepped = None

    def tick(self, real_dt):
        if self.paused or real_dt <= 0.0:
            return 0
        return self.accumulator.advance(real_dt, self._on_step)

    def _on_step(self, dt):
        self.total_steps += 1
        self.simulated_time += dt
        if self.encounter is not None:
            self.encounter.step_fixed(dt)
        if self.stepped is not None:
            self.stepped(dt)

    def step_exact(self, steps):
        for _ in range(steps):
            self._on_step(FIXED_STEP)

    def reset(self):
        self.accumulator.acc = 0.0
        self.total_steps = 0
        self.simulated_time = 0.0
        self.paused = False


# =============================================================================
# 测试用事件记录器
# =============================================================================

class Recorder(NullEvents):
    """记录内核抛出的全部事件，供断言检查。"""

    def __init__(self, encounter=None):
        self.enc = encounter
        # 不挂 Encounter 时（例如单独驱动 BossController 的用例）由调用方自己报步号：
        # 每次 update 之前 rec.tick() 一下。否则事件只能记成 -1，时间轴就成了负数。
        self.manual_step = 0
        self.hits = []              # (step, attacker_id, dmg)
        self.blocked = 0            # 被闸门拦下的次数
        self.deaths = []
        self.phases = []            # (step, phase)
        self.summons = []           # (step, n)
        self.shockwaves = []        # (step, radius, center)
        self.knockbacks = []

    def tick(self):
        """手动驱动模式下推进一个步号（与 Encounter.step_count 一样从 1 起）。"""
        self.manual_step += 1
        return self.manual_step

    def _step(self):
        return self.enc.step_count if self.enc is not None else self.manual_step

    def on_hit(self, attacker, defender, dmg, applied):
        if applied:
            self.hits.append((self._step(), attacker.id if attacker else -1, dmg))
        else:
            self.blocked += 1

    def on_enemy_death(self, e):
        self.deaths.append((self._step(), e.id))

    def on_boss_phase(self, phase):
        self.phases.append((self._step(), phase))

    def on_summon(self, n):
        self.summons.append((self._step(), n))

    def on_shockwave(self, radius, center):
        self.shockwaves.append((self._step(), radius, Vec2(center.x, center.y)))

    def on_knockback(self, e, impulse):
        self.knockbacks.append((self._step(), e.id, impulse))


# =============================================================================
# 场景构建工具
# =============================================================================

PLAYER_HUGE_HP = 1e9


def build_pinned_encounter(n_enemies, contact_damage=1.0):
    """
    构建「n 只敌人钉在玩家接触范围内」的场景。

    【为什么把速度设成 0】
    本组用例要测的是**两层闸门的量化行为**，不是走位。留着移动速度，敌人会在
    突进阶段以 3.2 倍速冲过玩家再折返，接触与否变成位移噪声的函数，
    4.300 次/s 这条基线就没法复现了。速度归零 ⇒ 恒定接触 ⇒ 只剩闸门在起作用，
    这正是与 T0 simulate_lockstep 逐位对齐的前提。
    """
    enc = Encounter()
    rec = Recorder(enc)
    enc.events = rec

    player = create_player(0, PLAYER_HUGE_HP, Vec2(0.0, 0.0))
    enc.set_player(player)

    for i in range(n_enemies):
        deg = 360.0 * i / max(1, n_enemies)
        pos = VEC_RIGHT.rotated_deg(deg) * 20.0        # 20 < TOUCH_RANGE(44)
        e = create_enemy(i + 1, pos, 1e9, contact_damage, 0.0)
        e.ai.spawn_pos = pos
        enc.add(e)

    return enc, rec, player


def run_lockstep(enc, duration=20.0):
    """按固定步直接推进（等价于 Godot 60Hz headless 测试台）。"""
    steps = int(round(duration / FIXED_STEP))
    for _ in range(steps):
        enc.step_fixed(FIXED_STEP)
    return steps


def run_at_fps(enc, fps, duration=20.0):
    """真实帧率驱动：deltaTime = 1/fps 喂累加器，逻辑仍落在 1/60 网格。"""
    sched = CombatScheduler(enc)
    frames = int(round(duration * fps))
    dt = 1.0 / fps
    for _ in range(frames):
        sched.tick(dt)
    return sched.total_steps


def t0_reference_hit_steps(n_sources, duration=20.0):
    """
    T0 侧参照序列：直接用 wcore_selfcheck.WCoreState 跑同样的锁步。
    返回 [(step_index, source_index), ...]。
    """
    w = WCoreState()
    w.hp_max = PLAYER_HUGE_HP
    w.hp = PLAYER_HUGE_HP
    out = []
    steps = int(round(duration / FIXED_STEP))
    for i in range(steps):
        w.tick(FIXED_STEP)
        for sid in range(n_sources):
            if w.take_damage_from(sid, 1.0):
                out.append((i, sid))
    return out


# =============================================================================
# 断言框架（与 T0 同风格）
# =============================================================================

_results = []


def check(name, ok, detail=""):
    _results.append((name, bool(ok), detail))
    print("  [%s] %-52s %s" % ("PASS" if ok else "FAIL", name, detail))
    return ok


def approx(a, b, tol):
    return abs(a - b) <= tol


def close(a, b):
    return math.isclose(a, b, rel_tol=REL_TOL, abs_tol=1e-9)


def section(title):
    print("")
    print("-" * 78)
    print("  " + title)
    print("-" * 78)


# =============================================================================
# T1-01 Vec2 几何
# =============================================================================

def test_t1_01_vec2():
    section("T1-01 Vec2 几何（对拍 Godot Vector2）")

    a = Vec2(0.0, 0.0)
    b = Vec2(3.0, 4.0)
    check("T1-01a distance_to(3,4)=5", close(a.distance_to(b), 5.0),
          "实测 %.9f" % a.distance_to(b))

    check("T1-01b dot((1,2),(3,4))=11", close(Vec2(1, 2).dot(Vec2(3, 4)), 11.0),
          "实测 %.9f" % Vec2(1, 2).dot(Vec2(3, 4)))

    ang = Vec2(1, 0).angle_deg_between(Vec2(0, 1))
    check("T1-01c 夹角(1,0)^(0,1)=90°", close(ang, 90.0), "实测 %.9f°" % ang)

    # 共线时 dot/(l1*l2) 会因浮点误差越界，必须 clamp 而不是让 acos 返回 NaN
    ang0 = Vec2(1, 0).angle_deg_between(Vec2(3, 0))
    ang180 = Vec2(1, 0).angle_deg_between(Vec2(-5, 0))
    check("T1-01d 共线夹角 0°/180° 不 NaN",
          close(ang0, 0.0) and close(ang180, 180.0),
          "实测 %.6f° / %.6f°" % (ang0, ang180))

    # TOUCH_RANGE 判定边界：44 命中，44+ε 落空
    at_edge = Vec2(0, 0).distance_to(Vec2(TOUCH_RANGE, 0.0))
    over_edge = Vec2(0, 0).distance_to(Vec2(TOUCH_RANGE + 0.001, 0.0))
    check("T1-01e TOUCH_RANGE=44 判定边界",
          at_edge <= TOUCH_RANGE and over_edge > TOUCH_RANGE,
          "边界 %.6f<=44, 越界 %.6f>44" % (at_edge, over_edge))

    r = VEC_RIGHT.rotated_deg(90.0)
    check("T1-01f rotated_deg(90)=(0,1)",
          abs(r.x) < 1e-9 and close(r.y, 1.0), "实测 %s" % r)

    check("T1-01g 零向量 normalized/direction_to 退化为零",
          Vec2().normalized().is_zero() and Vec2(5, 5).direction_to(Vec2(5, 5)).is_zero(),
          "无 NaN")

    # 击退初速：v0 = sqrt(2·1400·48)
    v0 = knockback_impulse(VEC_RIGHT, POISE_BREAK_KNOCKBACK_DIST).length()
    expect_v0 = math.sqrt(2.0 * KNOCKBACK_FRICTION * POISE_BREAK_KNOCKBACK_DIST)
    check("T1-01h 击退初速 v0=sqrt(2·1400·48)", close(v0, expect_v0),
          "实测 %.6f px/s" % v0)


# =============================================================================
# T1-02 / T1-03 / T1-04 频率与倍率
# =============================================================================

def test_t1_02_solo():
    section("T1-02 单挑频率（1 敌接触，走完整内核链路）")
    enc, rec, _ = build_pinned_encounter(1)
    run_lockstep(enc, 20.0)
    freq = len(rec.hits) / 20.0
    check("T1-02 单挑频率 = 1.700 次/s (±%.2f)" % FREQ_TOL,
          approx(freq, FREQ_SOLO, FREQ_TOL),
          "实测 %.3f 次/s（%d 次/20s，被闸门拦 %d 次）" % (freq, len(rec.hits), rec.blocked))
    return freq


def test_t1_03_swarm():
    section("T1-03 围攻频率（4 敌接触，同链路）")
    freqs = {}
    for n in (1, 2, 3, 4, 8):
        enc, rec, _ = build_pinned_encounter(n)
        run_lockstep(enc, 20.0)
        freqs[n] = len(rec.hits) / 20.0
    check("T1-03 围攻频率 = 4.300 次/s (±%.2f)" % FREQ_TOL,
          approx(freqs[4], FREQ_SWARM, FREQ_TOL),
          "实测 %.3f 次/s" % freqs[4])
    check("T1-03b 频率表随源数单调饱和",
          freqs[1] < freqs[2] < freqs[3] and approx(freqs[3], freqs[4], 1e-9)
          and approx(freqs[4], freqs[8], 1e-9),
          "  ".join("%d源=%.3f" % (n, freqs[n]) for n in (1, 2, 3, 4, 8)))
    return freqs


def test_t1_04_ratio(freq_solo, freqs):
    section("T1-04 围攻倍率")
    ratio = freqs[4] / freq_solo if freq_solo > 0 else 0.0
    check("T1-04 围攻/单挑 = 2.53x (±%.2f)" % MULT_TOL,
          approx(ratio, SWARM_MULT, MULT_TOL),
          "实测 %.4fx（%.3f / %.3f）" % (ratio, freqs[4], freq_solo))
    return ratio


# =============================================================================
# T1-05 高刷不变性
# =============================================================================

def test_t1_05_frame_rate():
    section("T1-05 高刷不变性（144Hz / 30Hz 喂 Tick，逻辑仍走 1/60）")
    base = {}
    for n in (1, 4):
        enc, rec, _ = build_pinned_encounter(n)
        run_lockstep(enc, 20.0)
        base[n] = len(rec.hits) / 20.0

    rows = []
    ok = True
    for fps in (30, 60, 144, 240):
        for n in (1, 4):
            enc, rec, _ = build_pinned_encounter(n)
            steps = run_at_fps(enc, fps, 20.0)
            f = len(rec.hits) / 20.0
            rows.append("%dHz/%d源=%.3f(%d步)" % (fps, n, f, steps))
            if not approx(f, base[n], 1e-9):
                ok = False
    check("T1-05 频率与帧率无关（与 60Hz 锁步逐位一致）", ok, "  ".join(rows))


# =============================================================================
# T1-06 减法承伤
# =============================================================================

def test_t1_06_subtractive():
    section("T1-06 减法承伤 real = max(1, d − max(0, armor − break_def))")

    cases = [
        # (raw, armor, break_def, 期望)
        (4.0, 10.0, 0.0, 1.0),      # 打不动也至少掉 1，杜绝免疫悬崖
        (20.0, 10.0, 0.0, 10.0),
        (100.0, 5.0, 0.0, 95.0),
        (20.0, 10.0, 4.0, 14.0),    # 破防 4 ⇒ 有效护甲 6
        (20.0, 10.0, 30.0, 20.0),   # 破防超过护甲，有效护甲夹到 0（不会变成负护甲加伤）
    ]
    ok = True
    detail = []
    for raw, armor, bd, expect in cases:
        got = preview_subtractive(raw, armor, bd)
        detail.append("d=%g/a=%g/bd=%g→%g" % (raw, armor, bd, got))
        if not close(got, expect):
            ok = False
    check("T1-06a 减法公式（含 floor=1 与破防夹取）", ok, "  ".join(detail))

    # 实际扣血路径
    e = create_enemy(1, Vec2(), 100.0, 0.0, 0.0)
    e.armor = 10.0
    e.apply_enemy_damage(20.0, 0.0)
    e.apply_enemy_damage(4.0, 0.0)
    check("T1-06b 连续扣血 100−10−1=89", close(e.hp, 89.0), "实测 hp=%.4f" % e.hp)

    # 血量夹在 0，不出负数
    e2 = create_enemy(2, Vec2(), 5.0, 0.0, 0.0)
    e2.apply_enemy_damage(999.0, 0.0)
    check("T1-06c 血量夹到 0 不为负", close(e2.hp, 0.0) and not e2.is_alive,
          "实测 hp=%.4f" % e2.hp)

    # 玩家侧是除法模型，与敌人侧刻意不同构
    keep = 1.0 - mitigation_of(50.0, DEF_SOFTCAP_K)
    check("T1-06d 玩家侧除法减伤 def=50 ⇒ keep=2/3", close(keep, 1.0 - 50.0 / 150.0),
          "实测 keep=%.9f" % keep)


# =============================================================================
# T1-07 AI 状态转移
# =============================================================================

def _probe_state(dist, strike_cd=0.0):
    """把一只敌人放在距玩家 dist 处，跑一步取状态。"""
    e = create_enemy(1, Vec2(dist, 0.0), 100.0, 1.0, 70.0)
    e.ai.spawn_pos = Vec2(dist, 0.0)
    e.ai.strike_cd = strike_cd
    e.ai.update(FIXED_STEP, Vec2(0.0, 0.0), Vec2())
    return e.ai.state


def test_t1_07_ai_states():
    section("T1-07 AI 状态转移（LEASH=480 / STRIKE_DIST=160）")

    cases = [
        (600.0, STATE_PATROL, "远超牵引"),
        (480.1, STATE_PATROL, "刚出牵引"),
        (480.0, STATE_CHASE, "牵引边界内（> 才脱战）"),
        (300.0, STATE_CHASE, "中距"),
        (160.1, STATE_CHASE, "刚出突进距离"),
        (160.0, STATE_STRIKE, "突进距离边界（<= 即进）"),
        (50.0, STATE_STRIKE, "贴脸"),
    ]
    ok = True
    detail = []
    for dist, expect, tag in cases:
        got = _probe_state(dist)
        detail.append("%.1f→%s" % (dist, STATE_NAME[got]))
        if got != expect:
            ok = False
    check("T1-07a 三态切换点精确", ok, "  ".join(detail))

    # STRIKE 冷却期间即使贴脸也只能 CHASE
    got = _probe_state(50.0, strike_cd=2.0)
    check("T1-07b 冷却中贴脸也只 CHASE", got == STATE_CHASE, "实测 %s" % STATE_NAME[got])

    # PATROL 回锚：离锚点远 ⇒ 30% 速度往回走；进 20 像素 ⇒ 立刻停
    # （玩家都在 1000 px 外，两次都必然是 PATROL；变的只有"离锚点多远"）
    e = create_enemy(1, Vec2(1000.0, 0.0), 100.0, 1.0, 70.0)
    e.ai.spawn_pos = Vec2(1300.0, 0.0)       # 距锚点 300 > 20 ⇒ 该回家
    e.ai.update(FIXED_STEP, Vec2(0.0, 0.0), Vec2())
    v_far = e.ai.cmd_velocity.length()
    e.position = Vec2(1000.0, 0.0)
    e.ai.spawn_pos = Vec2(1005.0, 0.0)       # 距锚点 5 < 20 ⇒ 到家了，别抖
    e.ai.update(FIXED_STEP, Vec2(0.0, 0.0), Vec2())
    v_near = e.ai.cmd_velocity.length()
    check("T1-07c PATROL 以 30% 速度回锚、20px 内即停",
          close(v_far, 70.0 * AI_PATROL_SPEED_MULT) and close(v_near, 0.0),
          "远端 %.4f px/s，锚点内 %.4f px/s" % (v_far, v_near))


# =============================================================================
# T1-08 STRIKE 时序
# =============================================================================

def test_t1_08_strike_timing():
    section("T1-08 STRIKE 时序（windup 0.35 → dash 0.25 → cd 3.5）")

    e = create_enemy(1, Vec2(0.0, 0.0), 100.0, 10.0, 100.0)
    e.ai.spawn_pos = Vec2()
    target = Vec2(100.0, 0.0)      # dist=100 ≤ 160 ⇒ 直接进 STRIKE

    windup_steps = 0
    dash_steps = 0
    dash_mults = []
    dash_speeds = []
    started = False

    for _ in range(200):
        e.ai.update(FIXED_STEP, target, Vec2())
        if e.ai.cmd_strike_started:
            started = True
        if e.ai.strike_phase == PHASE_WINDUP:
            windup_steps += 1
        if e.ai.cmd_dashing:
            dash_steps += 1
            dash_mults.append(e.ai.cmd_damage_mult)
            dash_speeds.append(e.ai.cmd_velocity.length())
        if e.ai.cmd_strike_ended:
            break

    windup_t = windup_steps * FIXED_STEP
    dash_t = dash_steps * FIXED_STEP

    check("T1-08a 进入 STRIKE 抛 strike_started", started, "")
    check("T1-08b windup ≈ 0.35s（±1 逻辑步）",
          abs(windup_t - AI_STRIKE_WINDUP) <= FIXED_STEP + 1e-9,
          "实测 %d 步 = %.5fs（配置 %.2fs）" % (windup_steps, windup_t, AI_STRIKE_WINDUP))
    check("T1-08c dash ≈ 0.25s（±1 逻辑步）",
          abs(dash_t - AI_STRIKE_DASH) <= FIXED_STEP + 1e-9,
          "实测 %d 步 = %.5fs（配置 %.2fs）" % (dash_steps, dash_t, AI_STRIKE_DASH))
    check("T1-08d 突进期速度 = 基础 ×3.2",
          len(dash_speeds) > 0 and all(close(s, 100.0 * AI_STRIKE_SPEED_MULT) for s in dash_speeds),
          "实测 %.4f px/s" % (dash_speeds[0] if dash_speeds else -1.0))
    check("T1-08e 突进期伤害倍率恒为 1.5（含最后一步 · D-2）",
          len(dash_mults) > 0 and all(close(m, AI_STRIKE_DMG_MULT) for m in dash_mults),
          "实测 %d 步全部 = %.2f" % (len(dash_mults), dash_mults[0] if dash_mults else -1.0))
    check("T1-08f 突进结束落 CD 3.5s 且回到 CHASE",
          close(e.ai.strike_cd, AI_STRIKE_CD) and e.ai.state == STATE_CHASE,
          "实测 cd=%.4f state=%s" % (e.ai.strike_cd, STATE_NAME[e.ai.state]))

    # 蓄力期不位移 —— 这是玩家的反应窗口，动了就等于没有窗口
    e2 = create_enemy(2, Vec2(0.0, 0.0), 100.0, 10.0, 100.0)
    moved = 0.0
    for _ in range(10):
        e2.ai.update(FIXED_STEP, target, Vec2())
        moved += e2.ai.cmd_velocity.length()
    check("T1-08g 蓄力期零位移（反应窗口成立）", close(moved, 0.0),
          "前 10 步累计速度 %.6f" % moved)


# =============================================================================
# T1-09 / T1-10 / T1-11 BOSS
# =============================================================================

def _make_boss(hp_max=1000.0, speed=80.0, contact=5.0):
    c = Combatant()
    c.id = 99
    c.faction = FACTION_ENEMY
    c.kind = "blood"
    c.display_name = "测试首领"
    c.level = 10
    c.is_boss = True
    c.hp_max = hp_max
    c.hp = hp_max
    c.contact_damage = contact
    c.poise_max = 60.0 * BOSS_POISE_MULT
    c.poise = c.poise_max
    boss = BossController(c)
    boss.speed_base = speed
    c.ai = boss
    return c, boss


def test_t1_09_boss_phase():
    section("T1-09 BOSS 阶段阈值（0.65 / 0.30，只降不升，各触发一次）")

    c, boss = _make_boss()
    rec = Recorder()
    boss.events = rec
    far = Vec2(10000.0, 0.0)       # 拉远，避免 AI 干扰

    seq = []
    for ratio in (0.70, 0.66, 0.65, 0.50, 0.31, 0.30, 0.10):
        c.hp = c.hp_max * ratio
        boss.update(FIXED_STEP, far, Vec2())
        seq.append("%.2f→P%d" % (ratio, boss.phase + 1))

    check("T1-09a 阈值判定正确（<=0.65 入 P2，<=0.30 入 P3）",
          boss.phase == BOSS_P3, "  ".join(seq))
    check("T1-09b 阶段事件各触发一次（共 2 次）",
          len(rec.phases) == 2 and rec.phases[0][1] == BOSS_P2 and rec.phases[1][1] == BOSS_P3,
          "实测 %d 次: %s" % (len(rec.phases), [p for _, p in rec.phases]))

    # 只降不升：回血不该把阶段退回去
    c.hp = c.hp_max
    boss.update(FIXED_STEP, far, Vec2())
    check("T1-09c 回满血不回退阶段（只降不升）",
          boss.phase == BOSS_P3 and len(rec.phases) == 2,
          "实测 P%d，事件仍 %d 次" % (boss.phase + 1, len(rec.phases)))

    # 一击跨两阈值：只抛 P3，不在同帧闪两条横幅
    c2, boss2 = _make_boss()
    rec2 = Recorder()
    boss2.events = rec2
    c2.hp = c2.hp_max * 0.20
    boss2.update(FIXED_STEP, far, Vec2())
    check("T1-09d 秒掉 80% 只抛 P3 一次事件",
          boss2.phase == BOSS_P3 and len(rec2.phases) == 1 and rec2.phases[0][1] == BOSS_P3,
          "实测 P%d，事件 %d 次" % (boss2.phase + 1, len(rec2.phases)))


def test_t1_10_boss_p2():
    section("T1-10 BOSS P2（速度×1.25，每 8.0s 召唤 2 只）")

    c, boss = _make_boss(hp_max=1000.0, speed=80.0)
    rec = Recorder()
    boss.events = rec
    boss.spawn = lambda req: create_enemy(1000 + req["index"], req["position"], 10.0, 1.0, 60.0)
    far = Vec2(10000.0, 0.0)

    c.hp = c.hp_max * 0.60          # 进 P2
    rec.tick()
    boss.update(FIXED_STEP, far, Vec2())

    check("T1-10a 进入 P2 ⇒ speed_mult=1.25 / atk_mult=1.00",
          close(boss.speed_mult, BOSS_P2_SPEED_MULT) and close(boss.atk_mult, BOSS_P2_ATK_MULT),
          "实测 speed=%.4f atk=%.4f 有效速度=%.2f" % (boss.speed_mult, boss.atk_mult, boss.effective_speed))

    steps = int(round(24.5 / FIXED_STEP))
    for _ in range(steps):
        rec.tick()
        boss.update(FIXED_STEP, far, Vec2())

    times = [s * FIXED_STEP for s, _ in rec.summons]
    counts = [n for _, n in rec.summons]
    # 容差按逻辑步表达：跨零点必然多走不足一步，但 D-4 修好后这个偏差不允许逐周期累加。
    ok_time = len(times) == 3 and all(
        abs(t - expect) <= FIXED_STEP + 1e-9 for t, expect in zip(times, (8.0, 16.0, 24.0)))
    check("T1-10b 每 8.0s 召唤一次，24.5s 内共 3 次（偏差 ≤1 逻辑步且不累加 · D-4）",
          ok_time, "实测触发于 %s 秒，间隔 %s" % (
              ["%.3f" % t for t in times],
              ["%.4f" % (b - a) for a, b in zip(times, times[1:])]))
    check("T1-10c 每次召唤 2 只且实体已入队",
          counts == [2, 2, 2] and len(boss.pending_spawns) == 6,
          "实测 counts=%s，pending=%d" % (counts, len(boss.pending_spawns)))


def test_t1_11_boss_p3():
    section("T1-11 BOSS P3（攻击×1.40 / 速度×1.40，每 6.0s 冲击波 r=200）")

    c, boss = _make_boss(hp_max=1000.0, speed=80.0)
    rec = Recorder()
    boss.events = rec
    far = Vec2(10000.0, 0.0)

    c.hp = c.hp_max * 0.25          # 直接进 P3
    rec.tick()
    boss.update(FIXED_STEP, far, Vec2())

    check("T1-11a 进入 P3 ⇒ atk_mult=1.40 / speed_mult=1.40",
          close(boss.atk_mult, BOSS_P3_ATK_MULT) and close(boss.speed_mult, BOSS_P3_SPEED_MULT),
          "实测 atk=%.4f speed=%.4f 有效速度=%.2f" % (boss.atk_mult, boss.speed_mult, boss.effective_speed))

    steps = int(round(18.5 / FIXED_STEP))
    for _ in range(steps):
        rec.tick()
        boss.update(FIXED_STEP, far, Vec2())

    times = [s * FIXED_STEP for s, _, _ in rec.shockwaves]
    radii = [r for _, r, _ in rec.shockwaves]
    ok_time = len(times) == 3 and all(
        abs(t - expect) <= FIXED_STEP + 1e-9 for t, expect in zip(times, (6.0, 12.0, 18.0)))
    check("T1-11b 每 6.0s 一次冲击波，18.5s 内共 3 次（偏差 ≤1 逻辑步且不累加 · D-4）",
          ok_time, "实测触发于 %s 秒，间隔 %s" % (
              ["%.3f" % t for t in times],
              ["%.4f" % (b - a) for a, b in zip(times, times[1:])]))
    check("T1-11c 冲击波半径 = 200",
          len(radii) == 3 and all(close(r, BOSS_P3_SHOCK_RADIUS) for r in radii),
          "实测 %s" % radii)

    # 冲击波命中判定：半径内命中，半径外落空（纯几何，不依赖碰撞体）
    enc = Encounter()
    rec2 = Recorder(enc)
    enc.events = rec2
    player = create_player(0, PLAYER_HUGE_HP, Vec2(150.0, 0.0))
    enc.set_player(player)
    src, _ = _make_boss(contact=20.0)
    hit_in = resolve_shockwave(src, player, Vec2(0.0, 0.0), BOSS_P3_SHOCK_RADIUS, rec2)
    player.position = Vec2(250.0, 0.0)
    player.wcore.reset()
    hit_out = resolve_shockwave(src, player, Vec2(0.0, 0.0), BOSS_P3_SHOCK_RADIUS, rec2)
    check("T1-11d 冲击波几何判定（150 命中 / 250 落空）",
          hit_in and not hit_out, "r=200")


# =============================================================================
# T1-12 分级缩放
# =============================================================================

def test_t1_12_scaling():
    section("T1-12 分级缩放（hp_scale / dmg_scale / 精英 / 词缀）")

    check("T1-12a hp_scale(1)=1.00, hp_scale(5)=1.72, hp_scale(10)=2.62",
          close(hp_scale(1), 1.0) and close(hp_scale(5), 1.72) and close(hp_scale(10), 2.62),
          "1+0.18(lv−1)")
    check("T1-12b dmg_scale(1)=1.00, dmg_scale(5)=1.56, dmg_scale(10)=2.26",
          close(dmg_scale(1), 1.0) and close(dmg_scale(5), 1.56) and close(dmg_scale(10), 2.26),
          "1+0.14(lv−1)")
    check("T1-12c 等级夹取 [1,60]",
          clamp_level(-5) == 1 and clamp_level(999) == 60 and clamp_level(30) == 30, "")

    # --- normal ---
    bridge = DifficultyBridge(player_def=0.0)
    cfg = ZoneEnemiesCfg(kinds=["witch"], hp_mult=1.0, elite_rate=0.0, affix_rate=0.0)
    e = bridge.build_enemy(cfg, base_stats_of("witch"), 5, PCG32(PCG_DEFAULT_SEED))
    expect_hp = 22.0 * 1.72
    check("T1-12d normal witch lv5: hp = 22×1.72 = 37.84",
          close(e.hp_max, expect_hp), "实测 %.6f" % e.hp_max)
    check("T1-12e 基础数值透传（speed=95 / armor=1 / poise=24）",
          close(e.move_speed, 95.0) and close(e.armor, 1.0) and close(e.poise_max, 24.0),
          "speed=%.2f armor=%.2f poise=%.2f" % (e.move_speed, e.armor, e.poise_max))

    # --- elite（elite_rate=1.0 必中）---
    b2 = DifficultyBridge(player_def=0.0)
    cfg_e = ZoneEnemiesCfg(kinds=["witch"], elite_rate=1.0, affix_rate=0.0)
    ee = b2.build_enemy(cfg_e, base_stats_of("witch"), 5, PCG32(PCG_DEFAULT_SEED))
    check("T1-12f 精英 HP×2.6 / ATK×1.5 / EXP×2.0",
          ee.is_elite and close(ee.hp_max, expect_hp * ELITE_HP_MULT)
          and close(ee.contact_damage, e.contact_damage * ELITE_ATK_MULT)
          and close(ee.exp_value, 6.0 * ELITE_EXP_MULT),
          "hp=%.4f atk=%.4f exp=%.1f" % (ee.hp_max, ee.contact_damage, ee.exp_value))

    # --- 词缀（逐个直接施加，避免依赖随机流分支）---
    rows = []
    ok = True
    for affix, tag in ((AFFIX_SWIFT, "swift"), (AFFIX_IRONHIDE, "ironhide"), (AFFIX_BLAZE, "blaze")):
        b3 = DifficultyBridge(player_def=0.0)
        c = b3.build_enemy(cfg, base_stats_of("witch"), 5, PCG32(PCG_DEFAULT_SEED))
        s0, a0, d0 = c.move_speed, c.armor, c.contact_damage
        apply_affix(c, affix)
        if affix == AFFIX_SWIFT:
            ok = ok and close(c.move_speed, s0 * AFFIX_SWIFT_SPEED_MULT)
            rows.append("swift speed %.2f→%.2f" % (s0, c.move_speed))
        elif affix == AFFIX_IRONHIDE:
            ok = ok and close(c.armor, a0 + AFFIX_IRONHIDE_ARMOR_ADD)
            rows.append("ironhide armor %.1f→%.1f" % (a0, c.armor))
        else:
            ok = ok and close(c.contact_damage, d0 * AFFIX_BLAZE_DMG_MULT)
            rows.append("blaze dmg %.4f→%.4f" % (d0, c.contact_damage))
    check("T1-12g 词缀 swift×1.35 / ironhide+12 / blaze×1.25", ok, "  ".join(rows))

    # swift 改的是 AI.speed_base（单一真源），不能只改 Combatant 的影子字段
    b4 = DifficultyBridge()
    cs = b4.build_enemy(cfg, base_stats_of("witch"), 1, PCG32(PCG_DEFAULT_SEED))
    apply_affix(cs, AFFIX_SWIFT)
    check("T1-12h swift 落在 AI.speed_base（速度单一真源）",
          close(cs.ai.speed_base, 95.0 * AFFIX_SWIFT_SPEED_MULT)
          and close(cs.ai.effective_speed, 95.0 * AFFIX_SWIFT_SPEED_MULT),
          "ai.speed_base=%.4f" % cs.ai.speed_base)

    # --- BOSS ---
    b5 = DifficultyBridge(player_def=0.0)
    boss_cfg = ZoneBossCfg(kind="witch", level_offset=2, hp_mult=10.0, atk_mult=1.6)
    zc = ZoneEnemiesCfg(kinds=["witch"], hp_mult=1.0)
    bc = b5.build_boss(boss_cfg, zc, 3, PCG32(PCG_DEFAULT_SEED))
    lv = 5
    expect_boss_hp = 22.0 * hp_scale(lv) * 10.0
    check("T1-12i BOSS lv=base+offset=5，hp=22×1.72×10=378.4，韧性×3.0",
          bc.level == lv and close(bc.hp_max, expect_boss_hp)
          and close(bc.poise_max, 24.0 * BOSS_POISE_MULT) and isinstance(bc.ai, BossController),
          "lv=%d hp=%.4f poise=%.1f exp=%.1f" % (bc.level, bc.hp_max, bc.poise_max, bc.exp_value))


# =============================================================================
# T1-13 模型 B 反调
# =============================================================================

def test_t1_13_model_b():
    section("T1-13 模型 B 反调（U1 后：d_eff ≈ player_hp/65，落在 [pHp/73.8, pHp/58.3]）")

    target = PLAYER_HP_MAX / DEFF_DIVISOR    # 260/65 = 4.0

    # U1 后的唯一靶心：player_hp/65 = 260/65 = 4.0。
    # 它是个常数——不随敌人种族、等级、血量、也不随玩家防御变化（防御在
    # solve 时以 keep 抵消、在承伤时以 mitigation 再乘回来，两处正好约掉）。
    target = compute_deff(PLAYER_HP_MAX)

    fails = []
    checked = 0
    for player_def in (0.0, 25.0, 50.0, 120.0):
        bridge = DifficultyBridge(player_def=player_def)
        keep = 1.0 - mitigation_of(player_def, DEF_SOFTCAP_K)
        for kind in ENEMY_SPECS:
            cfg = ZoneEnemiesCfg(kinds=[kind], hp_mult=1.0, elite_rate=0.0, affix_rate=0.0)
            for lv in (1, 5, 12, 30, 60):
                e = bridge.build_enemy(cfg, base_stats_of(kind), lv, PCG32(PCG_DEFAULT_SEED))
                deff = e.contact_damage * keep
                checked += 1
                # U1：靶心恒为 player_hp/65，与这只怪的种族/等级/血量都无关。
                # 「等级越高越痛」由血更厚 + 数量更多体现，不由每击伤害体现。
                if not close(deff, target):
                    fails.append("%s lv%d def%g: d_eff=%.6f vs %.6f"
                                 % (kind, lv, player_def, deff, target))
                elif not bridge.is_deff_in_target(e):
                    fails.append("%s lv%d def%g 越界" % (kind, lv, player_def))

    check("T1-13a d_eff == player_hp/65 = %.4f（%d 组：4 防御 × 4 种族 × 5 等级）"
          % (target, checked),
          not fails, "全部命中靶心" if not fails else "; ".join(fails[:3]))

    # 抽样展示反调出来的 atk_mult_eff：种子值（zones.json 的 atk_mult=1.0）确实被作废。
    # U1 后靶心恒定，baseAtk 随等级涨 ⇒ atk_mult_eff 随等级**反向**缩小，乘积恒为 4.0。
    rows = []
    for kind in ("blood", "witch", "sword", "alchemy"):
        base = base_stats_of(kind)
        atk = base["damage"] * dmg_scale(5)
        m = solve_effective_atk_mult(PLAYER_HP_MAX, atk, 0.0)
        rows.append("%s=%.4f" % (kind, m))
    check("T1-13b atk_mult 种子被反调取代（lv5 实际倍率）", True, "  ".join(rows))

    # 精英：ATK×1.5 让 d_eff 高于靶心 50%，HP×2.6 让它更耐打。
    # U1 后靶心不再随敌人血涨，所以比值就是干净的 ELITE_ATK_MULT。
    b2 = DifficultyBridge(player_def=0.0)
    cfg_e = ZoneEnemiesCfg(kinds=["sword"], elite_rate=1.0, affix_rate=0.0)
    ee = b2.build_enemy(cfg_e, base_stats_of("sword"), 12, PCG32(PCG_DEFAULT_SEED))
    ratio = ee.contact_damage / target
    check("T1-13c 精英 d_eff/靶心 = ATK 倍率 1.5（打得更痛，且 HP×2.6 更耐打）",
          close(ratio, ELITE_ATK_MULT),
          "实测 %.6f（靶心 %.4f，精英 d_eff %.4f，精英 hp %.1f）"
          % (ratio, target, ee.contact_damage, ee.hp_max))

    # BOSS：靶子同样取玩家血口径，再乘 boss.atk_mult
    b3 = DifficultyBridge(player_def=0.0)
    bc = b3.build_boss(ZoneBossCfg(kind="witch", level_offset=2, hp_mult=10.0, atk_mult=1.6),
                       ZoneEnemiesCfg(kinds=["witch"], hp_mult=1.0), 3, PCG32(PCG_DEFAULT_SEED))
    check("T1-13d BOSS d_eff = (player_hp/65)×1.6（不被 hp_mult=10 放大）",
          close(bc.contact_damage, target * 1.6),
          "实测 %.6f，若用 BOSS hp 反调则会是 %.6f"
          % (bc.contact_damage, bc.hp_max / DEFF_DIVISOR * 1.6))

    # U1 回归闸：修复前 d_eff≈0.58 被 DAMAGE_FLOOR=1 顶平，单挑 150+ 秒不死。
    # 这条守住「靶心必须真的落在 A1/A2 双窗口内」，防止将来有人把靶子改回敌人血。
    ttk_solo = PLAYER_HP_MAX / (target * FREQ_SOLO)
    ttk_swarm = PLAYER_HP_MAX / (target * FREQ_SWARM)
    check("T1-13e U1 回归：A2 单挑 %.1fs∈[35,60]、A1 围攻 %.1fs∈[10,16]"
          % (ttk_solo, ttk_swarm),
          35.0 <= ttk_solo <= 60.0 and 10.0 <= ttk_swarm <= 16.0,
          "d_eff=%.4f > DAMAGE_FLOOR=1，未被地板吃掉" % target)


# =============================================================================
# T1-14 端到端锁步
# =============================================================================

ZONE_ID = "zone_youhuang"
ZONE_BASE_LEVEL = 3


def test_t1_14_lockstep():
    section("T1-14 端到端锁步（固定种子 + 4 怪围攻，对拍 T0 承伤序列）")

    # --- 种子派生确定性 ---
    h = djb2(ZONE_ID)
    zs = zone_seed(ZONE_ID, False, 1)
    check("T1-14a zone 种子由 djb2 确定性派生",
          h == djb2(ZONE_ID) and zs == zone_seed(ZONE_ID, False, 1),
          "djb2('%s')=%d  zone_seed(visits=1)=%d" % (ZONE_ID, h, zs))

    # --- 同种子长出同一批怪 ---
    def build_roster(seed):
        rng = PCG32(seed)
        bridge = DifficultyBridge(player_def=0.0)
        cfg = ZoneEnemiesCfg(count=4, kinds=["blood", "witch"], hp_mult=1.0,
                             atk_mult=1.0, armor_add=0.0, elite_rate=0.05, affix_rate=0.20)
        out = []
        for _ in range(4):
            lv = roll_level(ZONE_BASE_LEVEL, rng)
            out.append(bridge.build_enemy_from_cfg(cfg, lv, rng))
        return out

    r1 = build_roster(PCG_DEFAULT_SEED)
    r2 = build_roster(PCG_DEFAULT_SEED)
    fp1 = ["%s/lv%d/hp%.4f/dmg%.4f/e%d/a%s"
           % (c.kind, c.level, c.hp_max, c.contact_damage, int(c.is_elite), AFFIX_NAME[c.affix])
           for c in r1]
    fp2 = ["%s/lv%d/hp%.4f/dmg%.4f/e%d/a%s"
           % (c.kind, c.level, c.hp_max, c.contact_damage, int(c.is_elite), AFFIX_NAME[c.affix])
           for c in r2]
    check("T1-14b seed=%d 两次生成的 4 怪完全一致" % PCG_DEFAULT_SEED, fp1 == fp2, "")
    for i, f in enumerate(fp1):
        print("        怪#%d  %s" % (i + 1, f))

    # 换种子必须长出不同世界，否则种子形同虚设
    r3 = build_roster(zs & 0xFFFFFFFF)
    fp3 = ["%s/lv%d/e%d/a%s" % (c.kind, c.level, int(c.is_elite), AFFIX_NAME[c.affix]) for c in r3]
    fp1s = ["%s/lv%d/e%d/a%s" % (c.kind, c.level, int(c.is_elite), AFFIX_NAME[c.affix]) for c in r1]
    check("T1-14c 换 zone 种子长出不同怪群（种子确实生效）", fp3 != fp1s,
          "zone 种子组: %s" % "  ".join(fp3))

    # --- 端到端：把这 4 只真怪投进 Encounter 跑满 20s ---
    enc = Encounter()
    rec = Recorder(enc)
    enc.events = rec
    enc.rng = PCG32(zs & 0xFFFFFFFF)
    player = create_player(0, PLAYER_HUGE_HP, Vec2(0.0, 0.0))
    enc.set_player(player)

    for i, e in enumerate(r1):
        pos = VEC_RIGHT.rotated_deg(360.0 * i / 4.0) * 20.0
        e.id = i + 1                       # 与 T0 的 source 0..3 一一对应
        e.position = pos
        e.ai.spawn_pos = pos
        e.ai.speed_base = 0.0              # 钉住接触，隔离位移噪声（见 build_pinned_encounter 注释）
        e.hp_max = 1e9
        e.hp = 1e9
        enc.add(e)

    run_lockstep(enc, 20.0)
    freq = len(rec.hits) / 20.0
    kernel_seq = [(s - 1, aid - 1) for s, aid, _ in rec.hits]   # step_count 从 1 起
    t0_seq = t0_reference_hit_steps(4, 20.0)

    check("T1-14d 4 怪围攻频率 = 4.300 次/s (±%.2f)" % FREQ_TOL,
          approx(freq, FREQ_SWARM, FREQ_TOL),
          "实测 %.3f 次/s（%d 次/20s）" % (freq, len(rec.hits)))
    check("T1-14e 承伤序列与 T0 wcore_selfcheck 逐位一致",
          kernel_seq == t0_seq,
          "两侧各 %d 次命中，(步号,来源) 序列完全相同" % len(kernel_seq))

    # 单挑对照 + 倍率
    enc1 = Encounter()
    rec1 = Recorder(enc1)
    enc1.events = rec1
    p1 = create_player(0, PLAYER_HUGE_HP, Vec2(0.0, 0.0))
    enc1.set_player(p1)
    solo = r1[0]
    s1 = create_enemy(1, Vec2(20.0, 0.0), 1e9, solo.contact_damage, 0.0)
    s1.ai.spawn_pos = s1.position
    enc1.add(s1)
    run_lockstep(enc1, 20.0)
    freq_solo = len(rec1.hits) / 20.0
    ratio = freq / freq_solo if freq_solo > 0 else 0.0

    check("T1-14f 端到端围攻倍率 = 2.53x (±%.2f)" % MULT_TOL,
          approx(ratio, SWARM_MULT, MULT_TOL),
          "实测 %.4fx（围攻 %.3f / 单挑 %.3f）" % (ratio, freq, freq_solo))

    return freq, freq_solo, ratio


# =============================================================================
# 附加：内核完整链路健全性（不计入 14 组，但失败同样阻断）
# =============================================================================

def test_extra_integration():
    section("附加 · 全链路健全性（霸体/击退/死亡/召唤入列）")

    # 破韧 → 硬直 → 击退
    enc = Encounter()
    rec = Recorder(enc)
    enc.events = rec
    player = create_player(0, 300.0, Vec2(0.0, 0.0))
    player.break_def = 0.0
    enc.set_player(player)
    e = create_enemy(1, Vec2(30.0, 0.0), 500.0, 0.0, 0.0)
    e.poise_max = 30.0
    e.poise = 30.0
    e.armor = 0.0
    enc.add(e)

    resolve_player_attack(player, e, 12.0, rec)      # poise 30→18
    broke1 = len(rec.knockbacks)
    resolve_player_attack(player, e, 12.0, rec)      # 18→6
    resolve_player_attack(player, e, 12.0, rec)      # 6→破韧，回满 30
    check("附加a 韧性累积到 0 才破韧（不是每下都破）",
          broke1 == 0 and len(rec.knockbacks) == 1 and close(e.poise, 30.0),
          "破韧 %d 次，破后韧性回满 %.1f" % (len(rec.knockbacks), e.poise))
    check("附加b 破韧施加硬直 0.20s 且写入击退速度",
          close(e.hit_stun_timer, POISE_BREAK_HITSTUN) and not e.knockback_vel.is_zero(),
          "hitstun=%.3f  v0=%.2f px/s" % (e.hit_stun_timer, e.knockback_vel.length()))

    # 击退位移在硬直期间照常推进
    x0 = e.position.x
    for _ in range(12):
        enc.step_fixed(FIXED_STEP)
    check("附加c 硬直期间击退位移照常推进",
          e.position.x > x0 + 1.0, "位移 %.2f px" % (e.position.x - x0))

    # 死亡 → 事件 + 出列
    before = len(enc.combatants)
    resolve_player_attack(player, e, 10000.0, rec)
    enc.step_fixed(FIXED_STEP)
    check("附加d 敌人死亡抛事件并被移出战场",
          len(rec.deaths) == 1 and len(enc.combatants) == before - 1,
          "死亡事件 %d 次，列表 %d→%d" % (len(rec.deaths), before, len(enc.combatants)))

    # BOSS 召唤真正进入 Encounter
    enc2 = Encounter()
    rec2 = Recorder(enc2)
    enc2.events = rec2
    enc2.rng = PCG32(PCG_DEFAULT_SEED)
    p2 = create_player(0, PLAYER_HUGE_HP, Vec2(0.0, 0.0))
    enc2.set_player(p2)
    cfg = ZoneEnemiesCfg(count=4, kinds=["witch"], elite_rate=0.0, affix_rate=0.0)
    bc = enc2.bridge.build_boss(ZoneBossCfg(kind="witch"), cfg, ZONE_BASE_LEVEL,
                                PCG32(PCG_DEFAULT_SEED))
    bc.position = Vec2(600.0, 0.0)
    bc.ai.spawn_pos = bc.position
    bc.ai.events = rec2
    bc.ai.spawn = lambda req: enc2.spawn_minion(cfg, req)
    bc.ai.speed_base = 0.0
    enc2.add(bc)

    bc.hp = bc.hp_max * 0.60        # 进 P2
    for _ in range(int(round(9.0 / FIXED_STEP))):
        enc2.step_fixed(FIXED_STEP)

    minions = [c for c in enc2.combatants if c.is_enemy and not c.is_boss]
    lv_ok = all(m.level == max(ENEMY_LEVEL_MIN, bc.level - 1) for m in minions)
    check("附加e BOSS P2 召唤物真正入列，且等级 = BOSS−1（R5）",
          len(minions) == 2 and lv_ok,
          "入列 %d 只，BOSS lv%d ⇒ 小怪 lv%s"
          % (len(minions), bc.level, [m.level for m in minions]))

    # 玩家不会被内核当尸体清掉
    enc3 = Encounter()
    p3 = create_player(0, 10.0, Vec2(0.0, 0.0))
    enc3.set_player(p3)
    p3.hp = 0.0
    enc3.step_fixed(FIXED_STEP)
    check("附加f 玩家死亡不被内核移出列表（死亡流程属上层）",
          p3 in enc3.combatants, "列表长度 %d" % len(enc3.combatants))


# =============================================================================
# 主流程
# =============================================================================

def main():
    print("=" * 78)
    print("  T1 战斗内核对拍自检 —— 对拍 Godot 原型基线 + T0 W-CORE")
    print("  FIXED_STEP=1/60=%.8f   TOUCH_RANGE=%.0f   PER_SOURCE_CD=%.4f   GLOBAL_GAP=%.4f"
          % (FIXED_STEP, TOUCH_RANGE, PER_SOURCE_HIT_CD, GLOBAL_HIT_GAP))
    print("  T0 内核来源: %s" % os.path.join(_CORE_DIR, "wcore_selfcheck.py"))
    print("=" * 78)

    test_t1_01_vec2()
    freq_solo = test_t1_02_solo()
    freqs = test_t1_03_swarm()
    ratio = test_t1_04_ratio(freq_solo, freqs)
    test_t1_05_frame_rate()
    test_t1_06_subtractive()
    test_t1_07_ai_states()
    test_t1_08_strike_timing()
    test_t1_09_boss_phase()
    test_t1_10_boss_p2()
    test_t1_11_boss_p3()
    test_t1_12_scaling()
    test_t1_13_model_b()
    e2e = test_t1_14_lockstep()
    test_extra_integration()

    passed = sum(1 for _, ok, _ in _results if ok)
    failed = len(_results) - passed

    print("")
    print("=" * 78)
    print("  汇总: %d / %d 通过" % (passed, len(_results)))
    print("  频率表: " + "  ".join("%d源=%.3f" % (n, freqs[n]) for n in (1, 2, 3, 4, 8)))
    print("  围攻/单挑倍率: %.4fx（基线 %.2fx）" % (ratio, SWARM_MULT))
    print("  端到端(T1-14): 围攻 %.3f 次/s  单挑 %.3f 次/s  倍率 %.4fx" % e2e)
    print("=" * 78)

    if failed:
        print("  结果: FAIL（%d 项未通过）" % failed)
        for name, ok, detail in _results:
            if not ok:
                print("    - %s  %s" % (name, detail))
        return 1
    print("  结果: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
