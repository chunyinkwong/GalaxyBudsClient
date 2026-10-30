using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreBluetooth;
using Foundation;
using GalaxyBudsClient.Platform;
using GalaxyBudsClient.Platform.Interfaces;
using GalaxyBudsClient.Platform.Model;
using Serilog;

namespace GalaxyBudsClient.iOS.Impl;

/// <summary>
/// CoreBluetooth based transport for iOS.
///
/// iOS does not expose the Bluetooth Classic RFCOMM/SPP sockets that the desktop and Android
/// backends use, so this implementation talks to the earbuds over BLE GATT instead:
/// the earbuds are identified by their CoreBluetooth identifier (the address returned by
/// <see cref="GetDevicesAsync"/> is a CBPeripheral UUID), a proprietary service is discovered,
/// notifications are enabled on its notify characteristic and outgoing protocol frames are written
/// to its write characteristic.
///
/// The Samsung GATT layout is not officially documented, so the service/characteristic pair is
/// discovered heuristically: the requested service UUID is preferred, otherwise the first service
/// that exposes both a writable and a notifiable characteristic is used.
/// </summary>
public class IosBluetoothService : IBluetoothService
{
    private const string Tag = "IosBluetoothService";

    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Delay between chunks of a fragmented write. Write-without-response packets are silently
    /// dropped when the controller's buffer is full, so outgoing data is paced conservatively.
    /// </summary>
    private const int WriteChunkDelayMs = 12;

    /// <summary>
    /// Standard GATT services which never carry the Samsung configuration protocol.
    /// </summary>
    private static readonly string[] IgnoredServiceUuids =
    [
        "00001800", // Generic Access
        "00001801", // Generic Attribute
        "00001802", // Immediate Alert
        "00001803", // Link Loss
        "00001804", // Tx Power
        "00001805", // Current Time
        "0000180a", // Device Information
        "0000180d", // Heart Rate
        "0000180f", // Battery Service
        "00001812", // Human Interface Device
        "00001813", // Scan Parameters
        "00001814", // Running Speed and Cadence
        "00001816", // Cycling Speed and Cadence
        "00001818", // Cycling Power
        "0000181a", // Environmental Sensing
        "0000181c", // User Data
        "00001826", // Fitness Machine
        "0000184e"  // LE Audio (media control, not the config channel)
    ];

    private readonly DispatchQueue _queue = new("me.timschneeberger.galaxybudsclient.ble");
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();

    private readonly Dictionary<string, CBPeripheral> _knownPeripherals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly CBCentralManager _central;
    private readonly CentralDelegate _centralDelegate;
    private readonly PeripheralDelegate _peripheralDelegate;

    private CBManagerState _centralState = CBManagerState.Unknown;
    private TaskCompletionSource<bool>? _stateSignal;
    private TaskCompletionSource<bool>? _connectSignal;
    private TaskCompletionSource<bool>? _servicesSignal;
    private TaskCompletionSource<bool>? _characteristicsSignal;
    private TaskCompletionSource<bool>? _notifySignal;

    private CBPeripheral? _peripheral;
    private CBCharacteristic? _writeCharacteristic;
    private CBCharacteristic? _notifyCharacteristic;
    private string? _requestedServiceUuid;
    private int _pendingCharacteristicDiscoveries;
    private bool _suppressDisconnectEvent;
    private volatile bool _isStreamConnected;

    public event EventHandler<BluetoothException>? BluetoothErrorAsync;
    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? RfcommConnected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<byte[]>? NewDataAvailable;

    public bool IsStreamConnected => _isStreamConnected;

    public IosBluetoothService()
    {
        _centralDelegate = new CentralDelegate(this);
        _peripheralDelegate = new PeripheralDelegate(this);
        // Instantiating the central manager triggers the iOS Bluetooth permission prompt.
        _central = new CBCentralManager(_centralDelegate, _queue);
    }

    public async Task<BluetoothDevice[]> GetDevicesAsync()
    {
        await EnsurePoweredOnAsync(CancellationToken.None).ConfigureAwait(false);

        lock (_stateLock)
        {
            // Drop previously scanned devices so the list only reflects the current environment,
            // but keep already connected peripherals so they don't disappear from the list.
            foreach (var pair in _knownPeripherals.ToList())
            {
                if (pair.Value.State != CBPeripheralState.Connected)
                    _knownPeripherals.Remove(pair.Key);
            }
        }

        StartScan();
        try
        {
            await Task.Delay(ScanDuration).ConfigureAwait(false);
        }
        finally
        {
            StopScan();
        }

        List<CBPeripheral> seen;
        lock (_stateLock)
        {
            seen = _knownPeripherals.Values.ToList();
        }

        var devices = seen.Select(BuildDevice).ToList();

        /* Sort the earbuds to the top: the user is looking for "Galaxy Buds ..." */
        return devices
            .OrderByDescending(d => d.Name.Contains("buds", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task ConnectAsync(string macAddress, string serviceUuid, CancellationToken cancelToken)
    {
        await EnsurePoweredOnAsync(cancelToken).ConfigureAwait(false);
        Connecting?.Invoke(this, EventArgs.Empty);
        await DetachAsync().ConfigureAwait(false);

        _requestedServiceUuid = NormalizeUuid(serviceUuid);

        var peripheral = await FindPeripheralAsync(macAddress, cancelToken).ConfigureAwait(false);
        if (peripheral == null)
        {
            throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                "The selected Bluetooth device is not available. Please make sure it is paired and connected.");
        }

        _peripheral = peripheral;
        peripheral.Delegate = _peripheralDelegate;

        Log.Information("{Tag}: Connecting to {Name} ({Id})", Tag,
            peripheral.Name ?? macAddress, peripheral.Identifier);

        _connectSignal = NewSignal();
        _central.ConnectPeripheral(peripheral, null as NSDictionary);

        try
        {
            await WaitAsync(_connectSignal.Task, ConnectTimeout, cancelToken,
                "Timed out while connecting to the earbuds.").ConfigureAwait(false);
        }
        catch (BluetoothException)
        {
            await DetachAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            await DiscoverTransportAsync(peripheral, cancelToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await DetachAsync().ConfigureAwait(false);
            throw;
        }

        _isStreamConnected = true;
        Log.Information("{Tag}: Connected, writing to {WriteUuid}, receiving on {NotifyUuid}", Tag,
            _writeCharacteristic?.UUID, _notifyCharacteristic?.UUID);
        RfcommConnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        _isStreamConnected = false;
        _suppressDisconnectEvent = true;

        try
        {
            await _writeLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try
            {
                await DetachAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Tag}: Error while disconnecting", Tag);
        }
        finally
        {
            _suppressDisconnectEvent = false;
        }
    }

    public async Task SendAsync(byte[] data)
    {
        var peripheral = _peripheral;
        var characteristic = _writeCharacteristic;

        if (peripheral == null || characteristic == null)
        {
            throw new BluetoothException(BluetoothException.ErrorCodes.SendFailed,
                "Error while sending data. Connection to the earbuds has been closed.");
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var maxLength = GetMaximumWriteLength(peripheral, characteristic);
            var offset = 0;
            while (offset < data.Length)
            {
                var length = Math.Min(maxLength, data.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(data, offset, chunk, 0, length);

                using var nsData = NSData.FromArray(chunk);
                peripheral.WriteValue(nsData, characteristic, CBCharacteristicWriteType.WithoutResponse);

                offset += length;

                if (offset < data.Length)
                {
                    /* Write-without-response has no completion callback, so give the radio time to drain */
                    if (!peripheral.CanSendWriteWithoutResponse)
                        await Task.Delay(WriteChunkDelayMs * 2).ConfigureAwait(false);
                    else
                        await Task.Delay(WriteChunkDelayMs / 2).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not BluetoothException)
        {
            Log.Error(ex, "{Tag}: Failed to write to the earbuds", Tag);
            throw new BluetoothException(BluetoothException.ErrorCodes.SendFailed,
                "Error while sending data. Connection to the earbuds has been closed.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /* ----------------------------- connection helpers ----------------------------- */

    private async Task DiscoverTransportAsync(CBPeripheral peripheral, CancellationToken cancelToken)
    {
        _pendingCharacteristicDiscoveries = 0;
        _servicesSignal = NewSignal();
        peripheral.DiscoverServices();

        await WaitAsync(_servicesSignal.Task, DiscoveryTimeout, cancelToken,
            "Timed out while discovering the earbuds' Bluetooth services.").ConfigureAwait(false);

        var services = peripheral.Services ?? Array.Empty<CBService>();
        if (services.Length == 0)
        {
            throw new BluetoothException(BluetoothException.ErrorCodes.UnsupportedDevice,
                "The selected device does not expose any Bluetooth services. Either the device is not a genuine Samsung Galaxy Buds device or it is not discoverable.");
        }

        /* Pick the service carrying the configuration protocol. The requested UUID (the classic
           RFCOMM UUID of the protocol) is usually not advertised over GATT, so fall back to any
           service that offers a writable and a notifiable characteristic. */
        var candidate = services.FirstOrDefault(s => NormalizeUuid(s.UUID.ToString()) == _requestedServiceUuid)
                        ?? services.FirstOrDefault(s => !IsIgnoredService(s));
        if (candidate != null)
            Log.Debug("{Tag}: Preferred service candidate: {Candidate}", Tag, candidate.UUID);

        _characteristicsSignal = NewSignal();
        _pendingCharacteristicDiscoveries = services.Length;
        foreach (var service in services)
            peripheral.DiscoverCharacteristics(service);

        await WaitAsync(_characteristicsSignal.Task, DiscoveryTimeout, cancelToken,
            "Timed out while discovering the earbuds' Bluetooth characteristics.").ConfigureAwait(false);

        SelectCharacteristics(services);

        if (_writeCharacteristic == null || _notifyCharacteristic == null)
        {
            throw new BluetoothException(BluetoothException.ErrorCodes.UnsupportedDevice,
                "The selected device does not support Samsung's configuration protocol. Either the device is not a genuine Samsung Galaxy Buds device or the wrong model has been chosen during manual setup.");
        }

        _notifySignal = NewSignal();
        peripheral.SetNotifyValue(true, _notifyCharacteristic);

        await WaitAsync(_notifySignal.Task, DiscoveryTimeout, cancelToken,
            "Timed out while subscribing to the earbuds' status notifications.").ConfigureAwait(false);
    }

    private void SelectCharacteristics(CBService[] services)
    {
        /* Score every service: prefer ones that expose both a writable and a notifiable
           characteristic, and prefer the write-without-response path since iOS has no
           write-with-response completion callback in its CoreBluetooth bindings. */
        (int Score, CBService Service, CBCharacteristic Write, CBCharacteristic Notify)? best = null;

        foreach (var service in services)
        {
            var characteristics = service.Characteristics ?? Array.Empty<CBCharacteristic>();
            var write = characteristics.FirstOrDefault(c => Has(c, CBCharacteristicProperties.WriteWithoutResponse))
                        ?? characteristics.FirstOrDefault(c => Has(c, CBCharacteristicProperties.Write));
            var notify = characteristics.FirstOrDefault(c => Has(c, CBCharacteristicProperties.Notify))
                         ?? characteristics.FirstOrDefault(c => Has(c, CBCharacteristicProperties.Indicate));

            Log.Debug("{Tag}: Service {Service}: {Count} characteristic(s)", Tag, service.UUID,
                characteristics.Length);

            if (write == null || notify == null)
                continue;

            var score = 0;
            if (NormalizeUuid(service.UUID.ToString()) == _requestedServiceUuid) score += 100;
            if (!IsIgnoredService(service)) score += 10;
            if (ReferenceEquals(write, notify)) score += 5;
            if (Has(write, CBCharacteristicProperties.WriteWithoutResponse)) score += 2;

            if (best == null || score > best.Value.Score)
                best = (score, service, write, notify);
        }

        if (best == null)
        {
            Log.Warning("{Tag}: No service with a writable and notifiable characteristic found", Tag);
            return;
        }

        _writeCharacteristic = best.Value.Write;
        _notifyCharacteristic = best.Value.Notify;
        Log.Debug("{Tag}: Selected service {Service} (write: {Write}, notify: {Notify})", Tag,
            best.Value.Service.UUID, best.Value.Write.UUID, best.Value.Notify.UUID);
    }

    private async Task<CBPeripheral?> FindPeripheralAsync(string address, CancellationToken cancelToken)
    {
        lock (_stateLock)
        {
            if (_knownPeripherals.TryGetValue(address, out var known))
                return known;
        }

        /* Ask iOS for the peripheral with that identifier; this works for devices the system already
           knows about, even if they are not advertising right now. */
        try
        {
            if (Guid.TryParse(address, out var identifier))
            {
                var retrieved = _central.RetrievePeripheralsWithIdentifiers([new NSUuid(identifier.ToString())]);
                var match = retrieved?.FirstOrDefault();
                if (match != null)
                {
                    Remember(match, match.Name);
                    return match;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Tag}: RetrievePeripheralsWithIdentifiers failed", Tag);
        }

        StartScan();
        try
        {
            var deadline = DateTime.UtcNow + ScanDuration;
            while (DateTime.UtcNow < deadline)
            {
                cancelToken.ThrowIfCancellationRequested();

                lock (_stateLock)
                {
                    if (_knownPeripherals.TryGetValue(address, out var found))
                        return found;
                }

                await Task.Delay(100, cancelToken).ConfigureAwait(false);
            }
        }
        finally
        {
            StopScan();
        }

        return null;
    }

    private async Task DetachAsync()
    {
        var peripheral = _peripheral;
        _peripheral = null;
        _writeCharacteristic = null;
        _notifyCharacteristic = null;
        _isStreamConnected = false;

        if (peripheral == null)
            return;

        try
        {
            /* Delegate is cleared so that tearing down doesn't raise a Disconnected event */
            var previousSuppression = _suppressDisconnectEvent;
            _suppressDisconnectEvent = true;
            try
            {
                peripheral.Delegate = null;
                if (peripheral.State != CBPeripheralState.Disconnected)
                    _central.CancelPeripheralConnection(peripheral);
            }
            finally
            {
                _suppressDisconnectEvent = previousSuppression;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Tag}: Error while cancelling the peripheral connection", Tag);
        }
    }

    private void StartScan()
    {
        try
        {
            if (_central.IsScanning)
                return;

            Log.Debug("{Tag}: Starting BLE scan", Tag);
            _central.ScanForPeripherals(null as CBUUID[], new NSDictionary());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Tag}: Failed to start BLE scan", Tag);
        }
    }

    private void StopScan()
    {
        try
        {
            if (_central.IsScanning)
            {
                Log.Debug("{Tag}: Stopping BLE scan", Tag);
                _central.StopScan();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Tag}: Failed to stop BLE scan", Tag);
        }
    }

    private async Task EnsurePoweredOnAsync(CancellationToken cancelToken)
    {
        if (_centralState is CBManagerState.Unknown or CBManagerState.Resetting)
        {
            var signal = NewSignal();
            _stateSignal = signal;

            /* The state may have been updated between the check above and the assignment */
            if (_centralState is not (CBManagerState.Unknown or CBManagerState.Resetting))
                signal.TrySetResult(true);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                timeout.CancelAfter(StateTimeout);
                await signal.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancelToken.IsCancellationRequested)
            {
                Log.Warning("{Tag}: Timed out while waiting for the Bluetooth state", Tag);
            }
        }

        switch (_centralState)
        {
            case CBManagerState.PoweredOn:
                return;
            case CBManagerState.Unauthorized:
                throw new BluetoothException(BluetoothException.ErrorCodes.NoAdaptersAvailable,
                    "Access to Bluetooth was denied. Please allow \"Galaxy Buds Manager\" to use Bluetooth in the iOS settings and try again.");
            case CBManagerState.Unsupported:
                throw new BluetoothException(BluetoothException.ErrorCodes.NoAdaptersAvailable,
                    "This device does not support Bluetooth Low Energy.");
            case CBManagerState.PoweredOff:
                throw new BluetoothException(BluetoothException.ErrorCodes.NoAdaptersAvailable,
                    "Bluetooth is not enabled");
            default:
                throw new BluetoothException(BluetoothException.ErrorCodes.NoAdaptersAvailable,
                    "Bluetooth is not available yet. Please try again.");
        }
    }

    private BluetoothDevice BuildDevice(CBPeripheral peripheral)
    {
        string? name;
        lock (_stateLock)
        {
            name = _knownNames.TryGetValue(peripheral.Identifier.ToString(), out var cachedName)
                ? cachedName
                : peripheral.Name;
        }

        if (string.IsNullOrWhiteSpace(name))
            name = "Unknown device";

        return new BluetoothDevice(
            name,
            peripheral.Identifier.ToString(),
            peripheral.State == CBPeripheralState.Connected,
            true,
            new BluetoothCoD(0x0400));
    }

    private void Remember(CBPeripheral peripheral, string? name)
    {
        var identifier = peripheral.Identifier.ToString();
        lock (_stateLock)
        {
            _knownPeripherals[identifier] = peripheral;
            if (!string.IsNullOrWhiteSpace(name))
                _knownNames[identifier] = name!;
        }
    }

    private static int GetMaximumWriteLength(CBPeripheral peripheral, CBCharacteristic characteristic)
    {
        try
        {
            var length = (int)peripheral.GetMaximumWriteValueLength(CBCharacteristicWriteType.WithoutResponse);
            return Math.Clamp(length, 20, 512);
        }
        catch (Exception)
        {
            return 20;
        }
    }

    private static bool Has(CBCharacteristic characteristic, CBCharacteristicProperties property)
        => characteristic.Properties.HasFlag(property);

    private static bool IsIgnoredService(CBService service)
    {
        var uuid = NormalizeUuid(service.UUID.ToString());
        return IgnoredServiceUuids.Any(ignored => uuid.StartsWith(ignored, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalises UUIDs so that short 16 bit UUIDs ("180f", "0000180f", "0000180f-0000-1000-8000-00805f9b34fb")
    /// and full 128 bit UUIDs can be compared.
    /// </summary>
    private static string NormalizeUuid(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return string.Empty;

        var value = uuid.Trim().ToLowerInvariant();

        /* CoreBluetooth reports standardised (SIG) UUIDs in their short form, e.g. "180F" */
        if (value.Length == 4)
            value = $"0000{value}";

        /* CBUUID.ToString() omits the dashes */
        return value.Length == 32 ? $"{value[..8]}-{value[8..12]}-{value[12..16]}-{value[16..20]}-{value[20..]}" : value;
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancelToken, string timeoutMessage)
    {
        var timeoutTask = Task.Delay(timeout, cancelToken);
        var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

        cancelToken.ThrowIfCancellationRequested();

        if (completed != task)
            throw new BluetoothException(BluetoothException.ErrorCodes.TimedOut, timeoutMessage);

        await task.ConfigureAwait(false);
    }

    /* ----------------------------- CoreBluetooth callbacks ----------------------------- */

    private void OnCentralStateUpdated(CBManagerState state)
    {
        Log.Debug("{Tag}: Central manager state: {State}", Tag, state);
        _centralState = state;

        if (state is not (CBManagerState.Unknown or CBManagerState.Resetting))
            _stateSignal?.TrySetResult(true);
    }

    private void OnPeripheralDiscovered(CBPeripheral peripheral, NSDictionary advertisementData)
    {
        /* Prefer the advertised local name over the cached peripheral name: buds advertise their
           model name ("Galaxy Buds2 Pro (ABCD)") while CBPeripheral.Name can be stale. */
        var name = peripheral.Name;
        if (advertisementData != null &&
            advertisementData[CBAdvertisement.DataLocalNameKey] is NSString nsName)
        {
            name = nsName.ToString();
        }

        Remember(peripheral, name);
    }

    private void OnPeripheralConnected(CBPeripheral peripheral)
    {
        Remember(peripheral, peripheral.Name);

        if (peripheral != _peripheral)
            return;

        Log.Information("{Tag}: Peripheral connected: {Name}", Tag, peripheral.Name ?? "?");
        _connectSignal?.TrySetResult(true);
    }

    private void OnPeripheralConnectFailed(CBPeripheral peripheral, NSError? error)
    {
        if (peripheral != _peripheral)
            return;

        Log.Error("{Tag}: Failed to connect: {Error}", Tag, error?.LocalizedDescription ?? "unknown error");
        _connectSignal?.TrySetException(new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
            "Connection failed. Your earbuds are either out of range or in use by the official manager app."));
    }

    private void OnPeripheralDisconnected(CBPeripheral peripheral, NSError? error)
    {
        if (peripheral != _peripheral)
            return;

        Log.Information("{Tag}: Peripheral disconnected: {Error}", Tag, error?.LocalizedDescription ?? "no error");

        _peripheral = null;
        _writeCharacteristic = null;
        _notifyCharacteristic = null;
        _isStreamConnected = false;

        if (_suppressDisconnectEvent)
            return;

        Disconnected?.Invoke(this,
            "Connection closed. The earbuds are either out of range or in use by the official manager app.");
    }

    private void OnServicesDiscovered(CBPeripheral peripheral, NSError? error)
    {
        if (error != null)
        {
            _servicesSignal?.TrySetException(new BluetoothException(
                BluetoothException.ErrorCodes.UnsupportedDevice,
                $"Failed to discover Bluetooth services: {error.LocalizedDescription}"));
            return;
        }

        var services = peripheral.Services ?? Array.Empty<CBService>();
        Log.Debug("{Tag}: Discovered {Count} service(s): {Services}", Tag, services.Length,
            string.Join(", ", services.Select(s => s.UUID.ToString())));

        _servicesSignal?.TrySetResult(true);
    }

    private void OnCharacteristicsDiscovered(CBPeripheral peripheral, CBService service, NSError? error)
    {
        if (error != null)
            Log.Warning("{Tag}: Failed to discover characteristics of {Service}: {Error}", Tag,
                service.UUID, error.LocalizedDescription);

        if (_characteristicsSignal == null)
            return;

        if (Interlocked.Decrement(ref _pendingCharacteristicDiscoveries) <= 0)
            _characteristicsSignal.TrySetResult(true);
    }

    private void OnCharacteristicValueUpdated(CBCharacteristic characteristic)
    {
        var value = characteristic.Value;
        if (value == null || value.Length == 0)
            return;

        var data = value.ToArray();
        _isStreamConnected = true;
        NewDataAvailable?.Invoke(this, data);
    }

    private void OnNotificationStateUpdated(CBCharacteristic characteristic, NSError? error)
    {
        if (error != null)
        {
            _notifySignal?.TrySetException(new BluetoothException(
                BluetoothException.ErrorCodes.UnsupportedDevice,
                "Failed to subscribe to the earbuds' status notifications."));
            return;
        }

        Log.Debug("{Tag}: Notification state for {Uuid}: {Enabled}", Tag, characteristic.UUID,
            characteristic.IsNotifying);
        _notifySignal?.TrySetResult(true);
    }

    /* ----------------------------- delegate bridge classes ----------------------------- */

    private sealed class CentralDelegate(IosBluetoothService owner) : CBCentralManagerDelegate
    {
        public override void UpdatedState(CBCentralManager central) => owner.OnCentralStateUpdated(central.State);

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSDictionary advertisementData, NSNumber RSSI)
            => owner.OnPeripheralDiscovered(peripheral, advertisementData);

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
            => owner.OnPeripheralConnected(peripheral);

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSError? error)
            => owner.OnPeripheralConnectFailed(peripheral, error);

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSError? error)
            => owner.OnPeripheralDisconnected(peripheral, error);
    }

    private sealed class PeripheralDelegate(IosBluetoothService owner) : CBPeripheralDelegate
    {
        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
            => owner.OnServicesDiscovered(peripheral, error);

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
            => owner.OnCharacteristicsDiscovered(peripheral, service, error);

        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic,
            NSError? error)
            => owner.OnCharacteristicValueUpdated(characteristic);

        public override void UpdatedNotificationState(CBPeripheral peripheral, CBCharacteristic characteristic,
            NSError? error)
            => owner.OnNotificationStateUpdated(characteristic, error);
    }
}
