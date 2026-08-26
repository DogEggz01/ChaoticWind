*Disclaimer: This mod is made with ChatGPT 5.6 Sol*

**Main feature**
- Disable trade wind (could be toggle)
- Add slider to control wind direction change magnitude, wind change timer and wind speed changing speed.
- Make open ocean bonus keep scaling with distance (Default off, could be toggle on)
- Add slider to control Open ocean + Storm bonus cap.
- Add Squall and Hurricane as custom storm, and revise storm wind behavior. Can be toggle off seperately.
**Custom Storm**
- Add 3 squalls of different wind strength. Squall cover small area(like GRC size), move fast and have very strong rain. Wind inside squall will change faster, both direction and magnitude.
  - Squall can appear in all region.
- Add 1 hurricane storm. Double the core storm area. Provide maximum 34 knots wind bonus. Have stronger than vanilla rain. Move slower.
  - Hurricane can only appear in Chronos, Emerald, Fire Fish lagoon region.
- Revise storm wind behavior
  - Now Storm bonus will start when you enter storm affect radius instead of fixed distance.
  - Maximum storm bonus wind will be reach much eariler than vanilla. (In vanilla you will need to be within 0.05 degree from storm center to receive max wind bonus)
  - When in storm affect radius, lerp speed set to 1(so wind reach target speed and direction faster), gust always atleast provide 1x bonus, and gust change timer become 10 sec(so each gust will last longer).
  - When multiple storm present, the stronger one will take effect.
**Value explained**
- Direction Chaos: Bigger means wind direction could change more drastically.
- Lerpspeed : Bigger means wind change speed faster. Game calculate gust at 0.5 sec timer, so if this value is high you will experience very abrupt and unstable wind speed change. Will also change how fast the wind settle in new direction.
  - Lerpspeed slider is disabled by default. You need to toggle it on before using it.
- Timer: Number you set will be average timer (unit is second). Actual timer range from 0.5x of the value to 2x of the value to set. For example a minimal value of 2 means wind change every 1~4 second. 
  - New timer will be applied after current timer is done, so you might need to wait for a while.
- if wind stuck after changing value, restart will usually fix it.
- Open Ocean Bonus: Vanilla game stop scaling at 4000 distance from land, 0.66x base wind as bonus wind. After enable the option it would scale upto 72000 (8 degree), at 1.52x base wind as bonus.
  - Before distance 4000, it will behave same as vanilla.
- Open ocean + Storm bonus cap: Vanilla default at 20, means Open Ocean bonus + storm wind will generate maximum 20 knots of speed as bonus.
- Storm relocate distance: Storm will relocate to another position after passing this set distance. Reduce it to increase the chance you met storm.

**Compatibility**
- Climate
  - When Custom wind on: Direction Chaos, Lerpspeed, Timer, Open ocean bonus and cap still work. Tradewind toggle will be disable. Custom storm behavior will be present even when climate custom wind is on.
- BorderExpander
  - Trade wind disable option will overide border expander's trade wind, so it could be toggle on/off. Direction Chaos will apply to synthetic latitude regions.


This mod doesn't modify base wind magitude
