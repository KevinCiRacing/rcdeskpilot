# Audio: engine loops and 3D sound on XAudio2

Status: ready-for-agent

## What to build

Port the sound layer to Vortice.XAudio2 + X3DAudio: WAV loading, looping engine sounds with continuous pitch (frequency-ratio) and volume control driven by throttle/RPM, 3D-positioned emitters tracking the aircraft against the listener camera, and one-shot effects (crash, gate, variometer, wind ambience). The public surface of the sound layer should let existing call sites port with minimal change.

## Acceptance criteria

- [ ] Engine sound loops seamlessly and pitch/volume track throttle continuously without clicks or drift
- [ ] 3D positioning: engine sound pans/attenuates correctly as the aircraft flies around the fixed pilot camera
- [ ] One-shots (crash, gate, variometer, wind) play correctly, including overlapping instances
- [ ] All existing WAV content plays unmodified

## Blocked by

- 04
