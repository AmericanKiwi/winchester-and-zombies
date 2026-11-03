WINCHESTER & ZOMBIES  -  a Nomad Drive mod
==========================================

Adds a working Winchester 1873 lever-action rifle and zombies that roam the
desert and attack players. Works in single player and multiplayer.

EVERYONE in a multiplayer session needs this mod installed, same version.
The host's settings decide zombie numbers and damage.

OFFICIAL DOWNLOAD: {{OFFICIAL_URL}}
Only download it from there. Share the link, not the zip: re-uploads are
not allowed (see LICENSE.txt) and could have been tampered with.


CHECK YOUR COPY
---------------
The official page lists the SHA-256 of the zip. To check yours, open
PowerShell in the folder with the zip and run:
  Get-FileHash .\WinchesterZombies_v{{VERSION}}.zip
The Hash it prints must match the one on the official page exactly.
Install.bat also checks every file against CHECKSUMS.txt and refuses to
install if anything was changed, removed or added.


INSTALL
-------
1. Close Nomad Drive.
2. Unzip this folder anywhere.
3. Double-click Install.bat. It finds your Steam copy of Nomad Drive and
   copies the mod in. (If it can't find it, paste your game folder path.)
4. Start Nomad Drive from Steam as usual.

Manual install: copy everything inside the "files" folder into your
Nomad Drive game folder (the one containing "Nomad Drive.exe").


PLAYING
-------
Rifle (pick it up like any item; it goes in your hotbar):
  Left mouse ......... fire (the lever is worked automatically after each shot)
  Right mouse (hold) . aim down the iron sights (your left hand lets go of the
                       forend while you aim, so your arm doesn't block the view)
  T .................. reload from your pocket, one round at a time
                       (fire to stop early)
  The rifle holds 7 rounds. Bottom-right shows rounds in the rifle and in
  your pocket while you hold it.
  A shot to the head kills a zombie outright; body shots take two.

Ammo:
  You can carry 7 spare rounds in your pocket. To fill it, hold the rifle,
  look at a box of .44-40 cartridges and hold the interact button (the same
  one you hold to put a battery in the grinder). Each box has 20 rounds and
  disappears when it's empty. Boxes can be picked up and carried in your
  hotbar like any small item; set one down to take rounds from it. Boxes
  stack on top of each other. They go down on floors, seats, tables and
  other boxes, not on walls.

Where to find them:
  A new world starts with one rifle and one ammo box per player slot, a few
  steps from where you start. Loot locations sometimes have a rifle (rare)
  or an ammo box (more common) among their other loot.

Host only:
  Page Up ............ spawn a loaded Winchester in front of you
  Delete ............. spawn an ammo box in front of you
  Home ............... spawn a zombie 20 m ahead
  End ................ pause / resume placing zombies at new places
  Page Down .......... reload the settings file without restarting

Zombies are placed once, as you travel: 20-40 at big locations with
buildings, 5-10 at small ones, and groups on the road between them. Places never
refill, so an area you've cleared stays clear for the session. Zombies chase
anyone within about 45 m and claw when close. Zombies far from every player
sleep until someone comes near, which keeps large numbers cheap to run.

Vehicles: zombies are solid. Hitting one faster than 10 mph kills it with a
thump that jolts the vehicle, and the part that hit it (bumper, tyre,
hood...) loses some condition, more the faster you go. Slower, you shove
them aside and knock them about. Tyres bump over bodies lying on the road,
and each body you roll over wears that tyre by 1-2%. Change the jolt, the
bump and the tyre wear under [Vehicles] in the settings file.

Zombies walk around rocks, buildings and vehicles, and keep their distance
from each other instead of bunching into one spot. Road groups mostly stand
in the road itself, so you can choose to run them down or steer round.

Blood: rifle hits spray blood and can drip on the ground, dead zombies leave
a pool where they fall, and a zombie you run over splatters the road where it
lands. Everyone in the session sees the same blood. Ground stains dry up
after 5 minutes (change or turn off under [Blood] in the settings file).

Blood on vehicles is painted into the bodywork with the game's own paint
system (like the spray can), splattered across the panels that hit the
zombie (more the faster you were going), so it follows the panel's shape. Because it's real paint it stays on and is saved with the
vehicle: spray-paint or grind over it to get rid of it. It counts as paint
for restoration, the same as a small dab from the spray can.


SETTINGS
--------
After the first launch, edit:
  Nomad Drive\BepInEx\config\com.americankiwi.nomaddrive.winchesterzombies.cfg
Rifle damage and capacity, pocket size, rounds per box, how many rifles and
boxes you start with, how often loot has them, zombie count, health, damage,
speed, spawn distances and all keys are in there. [Hands] sets where your
hands grip the rifle; Page Down applies changes while you play.


UNINSTALL
---------
Double-click Uninstall.bat. It removes the mod, and BepInEx too if no
other mods use it.


GOOD TO KNOW
------------
- Rifles, ammo boxes, the rounds in them and everyone's pocket are saved
  with the world, along with which loot locations have been searched.
  Zombies aren't saved; places get populated again after loading.
- A world saved before you installed the mod gets its starting rifles and
  boxes the first time you load it, next to the host.
- If you remove the mod, the world still loads; the game just says a few
  objects couldn't be restored (the rifles and boxes).
- Shots go where your crosshair points, from the hip or aimed.
- Sounds are simple placeholders.

Built with BepInEx 5 (LGPL-2.1). Includes Mono class libraries
(MIT licence) needed by BepInEx for this game. No game files are included.
