using System.Security.Cryptography;
using BleUnlockService.Config;
using BleUnlockService.Resources;

namespace BleUnlockService.Core;

// Version de un solo IRK: el servicio sigue un unico dispositivo
public sealed class RpaResolver
{
    private const int MaxCacheSize = 10_000;

    private readonly object _lock = new();
    // Cache direccion -> resultado; tambien guarda los fallos para no repetir AES
    private readonly Dictionary<ulong, bool> _cache = [];
    // Un Aes por orden de bytes, creado al fijar la clave y reutilizado: solo se usa bajo _lock
    private List<(IrkByteOrder Order, Aes Cipher)> _candidates = [];

    // Orden que realmente coincidio; null mientras no haya habido ninguna coincidencia
    public IrkByteOrder? MatchedOrder { get; private set; }

    public void SetKey(byte[] irk, IrkByteOrder order)
    {
        if (irk.Length != 16)
            throw new ArgumentException(ServiceStrings.Error_IrkLength, nameof(irk));

        var candidates = new List<(IrkByteOrder, Aes)>();
        if (order is IrkByteOrder.Normal or IrkByteOrder.Auto)
            candidates.Add((IrkByteOrder.Normal, CreateCipher(irk.ToArray())));
        if (order is IrkByteOrder.Reversed or IrkByteOrder.Auto)
            candidates.Add((IrkByteOrder.Reversed, CreateCipher(Enumerable.Reverse(irk).ToArray())));

        List<(IrkByteOrder Order, Aes Cipher)> previous;
        lock (_lock)
        {
            previous = _candidates;
            _candidates = candidates;
            _cache.Clear();
            MatchedOrder = null;
        }

        // Fuera del candado nadie puede seguir usando los anteriores
        foreach (var c in previous) c.Cipher.Dispose();
    }

    private static Aes CreateCipher(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    public bool Matches(ulong address, bool isRandom)
    {
        if (BleAddress.Classify(address, isRandom) != AddressKind.Resolvable) return false;

        lock (_lock)
        {
            if (_cache.TryGetValue(address, out var cached)) return cached;

            // RPA = prand (24 bits altos) || hash (24 bits bajos)
            uint prand = (uint)(address >> 24) & 0xFFFFFF;
            uint hash  = (uint)address & 0xFFFFFF;

            bool matched = false;
            foreach (var c in _candidates)
            {
                if (ComputeHash(c.Cipher, prand) == hash)
                {
                    matched = true;
                    MatchedOrder = c.Order;
                    break;
                }
            }

            if (_cache.Count >= MaxCacheSize) _cache.Clear();
            _cache[address] = matched;
            return matched;
        }
    }

    // Funcion ah(k, r) del Bluetooth Core Spec Vol 3 Part H 2.2.2, bytes con MSB primero:
    // ah = e(k, 13 bytes de relleno 0x00 || r) mod 2^24
    public static uint ComputeHash(byte[] irk, uint prand)
    {
        using var aes = CreateCipher(irk);
        return ComputeHash(aes, prand);
    }

    private static uint ComputeHash(Aes aes, uint prand)
    {
        Span<byte> block = stackalloc byte[16];
        block.Clear();
        block[13] = (byte)(prand >> 16);
        block[14] = (byte)(prand >> 8);
        block[15] = (byte)prand;

        Span<byte> output = stackalloc byte[16];
        aes.EncryptEcb(block, output, PaddingMode.None);
        return (uint)(output[13] << 16 | output[14] << 8 | output[15]);
    }
}
