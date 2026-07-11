# Audio: engine loops and 3D sound on XAudio2

Status: resolved

## What to build

Port the sound layer to Vortice.XAudio2 + X3DAudio: WAV loading, looping engine sounds with continuous pitch (frequency-ratio) and volume control driven by throttle/RPM, 3D-positioned emitters tracking the aircraft against the listener camera, and one-shot effects (crash, gate, variometer, wind ambience). The public surface of the sound layer should let existing call sites port with minimal change.

## Acceptance criteria

- [ ] Engine sound loops seamlessly and pitch/volume track throttle continuously without clicks or drift
- [ ] 3D positioning: engine sound pans/attenuates correctly as the aircraft flies around the fixed pilot camera
- [ ] One-shots (crash, gate, variometer, wind) play correctly, including overlapping instances
- [ ] All existing WAV content plays unmodified

## Blocked by

- 04

## Comments

Resolved in commit fa20d92. All four criteria verified programmatically (voice states, sample counters, DSP matrices) plus audibly during the test run. Notes: legacy log-scale volume mapping approximated as linear amplitude (volume/100 - perceptual difference at low settings, revisit if the sound dialog feels off); Pan matrix assumes stereo output for the panned path; doppler and RPM pitch compose in Sound3D.FrequencyRatio.
