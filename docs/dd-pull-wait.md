# DD combat start delay (v1.62.9)

Replaces the v1.62.7/8 distance gate and special retreat-follow behavior. No enemy-distance checks remain. On entering combat in DD, wait 3 seconds by default before AltMate starts BMR/RSR. Configurable in linked combat settings from 0 to 15 seconds; 0 disables. Shared and synchronized between clients.

During the delay AltMate-owned movement and combat automation are stopped. After the delay the pre-gate follow/combat behavior resumes. Reset on combat end, leader/territory change and link stop. Both leader and follower combat trigger this timer. Independently started plugins are not forcibly disabled. DD treasure-interaction guards from v1.62.6 remain.
