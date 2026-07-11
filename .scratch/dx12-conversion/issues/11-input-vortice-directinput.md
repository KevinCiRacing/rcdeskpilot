# Input: Transmitter + keyboard on Vortice.DirectInput

Status: claimed

## What to build

Port the input layer to Vortice.DirectInput: enumerate HID game controllers (the Transmitter is the primary device), read axes/buttons with the existing calibration/mapping semantics, and provide keyboard state. Re-encapsulate the input API so the MDX Key enum and raw keyboard-state indexing no longer leak into Sim code — Sim code consumes engine-level input abstractions (axis values, named actions/keys) only.

Preserve the existing channel-mapping and calibration settings format so users' transmitter setups keep working.

## Acceptance criteria

- [ ] A USB Transmitter enumerates and all its axes/buttons read correctly at flight-loop rates
- [ ] Keyboard flight (arrow keys etc.) works as a fallback, matching old key bindings
- [ ] Existing controller mapping/calibration settings load and apply unchanged
- [ ] No Microsoft.DirectX or Vortice input types appear outside the engine's input layer

## Blocked by

- 04
