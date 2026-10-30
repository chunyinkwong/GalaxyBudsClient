# GalaxyBudsClient.iOS

Experimental iOS head for GalaxyBudsClient (Avalonia). It reuses the shared UI/logic from
`../GalaxyBudsClient` and adds an iOS platform backend (`Impl/`) plus a CoreBluetooth Low Energy
transport for the Samsung configuration protocol.

## Requirements

- macOS with Xcode 26.6 and the .NET SDK pinned in `global.json` (`dotnet workload install ios`)
- iOS 15.0 or newer on the device
- Galaxy Buds that expose the configuration protocol over BLE

There is no iOS build on Windows/Linux — the `ios` workload only exists on macOS. The GitHub
Actions workflow `.github/workflows/compile-ios.yml` builds it on `macos-26`.

## Local build

```bash
dotnet workload install ios
dotnet publish GalaxyBudsClient.iOS/GalaxyBudsClient.iOS.csproj \
  -f net10.0-ios -c Release -r ios-arm64 -o bin_ios \
  -p:EnableCodeSigning=false
```

## CI artifact (unsigned IPA)

The `Compile iOS (unsigned IPA)` workflow publishes an **unsigned** IPA:

```
actions artifact: GalaxyBudsClient-ios-unsigned
file:             GalaxyBudsClient-ios-unsigned.ipa
```

An unsigned IPA cannot be installed directly. It has to be re-signed with *your* Apple ID or
developer certificate:

| Tool | Notes |
| --- | --- |
| [AltStore](https://altstore.io) / [SideStore](https://sidestore.io) | Free Apple ID, 7 day profile, wireless refresh |
| [Sideloadly](https://sideloadly.io) | Windows/macOS, free Apple ID or paid developer account |
| Xcode | `Devices and Simulators` → drop the IPA / `ios-deploy` |
| macOS + `codesign` | `codesign -f -s "Apple Development: …" --entitlements … Payload/*.app` |

Free provisioning profiles expire after 7 days (Apple ID) — re-install to refresh.

## Implementation notes

- `AppDelegate.cs` mirrors `GalaxyBudsClient.Android/MainActivity.cs`: it calls `Program.Startup`
  and injects `IosPlatformImplCreator` through `PlatformImpl.InjectExternalBackend`.
- `Impl/IosBluetoothService.cs` implements `IBluetoothService` on top of `CBCentralManager` /
  `CBPeripheral`. Writes always use `CBCharacteristicWriteType.WithoutResponse` (the .NET iOS
  bindings expose no write-with-response completion callback) and are chunked by
  `CBPeripheral.GetMaximumWriteValueLength`.
- Samsung's GATT layout is undocumented, so the transport *discovers* the service/characteristic
  pair at connect time: services are scanned, characteristics are discovered for each of them and
  the best writable + notifiable pair is selected (the SPP/RFCOMM UUID passed in by
  `BluetoothImpl` is preferred if a matching GATT service exists). If nothing usable is found the
  connect fails with `UnsupportedDevice`.
- The iOS "address" of a device is its `CBPeripheral.Identifier` (a random UUID per iOS install),
  not the Bluetooth MAC address.
- Not supported on iOS (same as Android): tray icon, hotkeys, autostart, scripting/experiments
  (`#if !Android && !IOS`), battery history database.
- `NSBluetoothAlwaysUsageDescription` is declared in `Info.plist`; the system permission prompt
  appears the first time the app creates its `CBCentralManager`.
