# DD pull wait (v1.62.7)

In DD with combat linking enabled, defer AltMate-owned BMR/RSR automation until a living, targetable combatant is within 5m (3D center distance) of the leader. Only enemies targeting the party or selected by the leader count. Keep nearby follow active while waiting, respecting casting/event safety restrictions (the generic pause-in-combat option does not suppress pull-wait leader following).

This delays all AltMate-started combat automation, including automated healing. It does not forcibly stop independently started plugins. Once released, stay released for the encounter; reset on combat end, leader/territory change or link stop. Additional pulls during the same encounter do not restart the gate. Follower-only aggro uses the same rule. Missing enemy/leader information keeps the gate closed.

Build and regression checks pass; in-game verification pending.

v1.62.8 fix: v1.62.7 incorrectly applied PauseLinkInCombat to pull waiting, preventing retreat-follow with the default setting enabled. Remove that condition while retaining IsBlocked safety checks.
