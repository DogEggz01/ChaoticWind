*Disclaimer: This mod is made with ChatGPT 5.6 Sol*

**Custom Storm have moved to another independent mod**


For people that don't want Simple Trade wind cycle: Install **1.3.4**

For people want Trade wind Cycle but only AA/EA wind cycle: Install **1.4.0**

For people want all trade wind cycle: Install **1.4.1**

## Main feature
- Trade wind could be toggle off.
- Add slider to control wind direction change magnitude, wind change timer and wind speed changing speed.
- Make open ocean bonus keep scaling with distance (Default off, could be toggle on)
- Add slider to control Open ocean + Storm bonus cap.
- Add Simple trade wind cycle. (Default off, need to be turn on in Configurator)
## Setting Value explained
- **Direction Chaos**: Bigger means wind direction could change more drastically.
- **Lerpspeed** : Bigger means wind change speed faster. Game calculate gust at 0.5 sec timer, so if this value is high you will experience very abrupt and unstable wind speed change. Will also change how fast the wind settle in new direction.
  - Lerpspeed slider is disabled by default. You need to toggle it on before using it.
- **Timer**: Number you set will be average timer (unit is second). Actual timer range from 0.5x of the value to 2x of the value to set. For example a minimal value of 2 means wind change every 1~4 second. 
  - New timer will be applied after current timer is done, so you might need to wait for a while.
  - if wind stuck after changing value, restart will usually fix it.
- **Open Ocean Bonus**: Vanilla game stop scaling at 4000 distance from land, 0.66x base wind as bonus wind. After enable the option it would scale upto 72000 (8 degree), at 1.52x base wind as bonus.
  - Before distance 4000, it will behave same as vanilla.
- **Open ocean + Storm bonus cap**: Vanilla default at 20, means Open Ocean bonus + storm wind will generate maximum 20 knots of speed as bonus.
## Simple Trade Wind Cycle
- latitude 30-33 will be dominant only by either Al'Ankh trade wind, or Emerald trade wind.
- It will start with Al'Ankh Tradewind. Every 20 days it will change to another one. There will be a 1 day transition period.
- Aestrin Trade wind will be change from vanilla WSW to WNW every 20 days also.
  - 0-19 day: AA wind, Aestrin WSW wind
  - 20 day: AA tranistion to EA wind, Aestrin transistion from WSW to WNW
  - 21-39: EA wind, Aestrin WNW wind
  - 40 day: EA transition to AA wind, Aestrin transition from WNW to WSW
  - 41-59: AA wind, Aestrin WSW wind
    ........ repeat.
- Won't take effect if Climate mod custom wind is on.
- Toggle it off/on might take a while for the wind to get to proper position.
## Compatibility
- Climate
  - When Custom wind on: Direction Chaos, Lerpspeed, Timer, Open ocean bonus and cap still work. Tradewind toggle will be disable.
- BorderExpander
  - Trade wind disable option will overide border expander's trade wind, so it could be toggle on/off. Direction Chaos will apply to synthetic latitude regions.


This mod doesn't modify base wind magitude
