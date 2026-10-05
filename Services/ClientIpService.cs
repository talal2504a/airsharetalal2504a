using System.Net;
using System.Net.Sockets;

namespace AirShare.Services;

/// <summary>
/// IP resolve karne ka interface. Controller isko DI se leta hai.
/// </summary>
public interface IClientIpService
{
    /// <summary>Caller ka "network id" — isi se shares group hote hain.</summary>
    string GetNetworkId(HttpContext context);
}

/// <summary>
/// IP resolution aur normalization ka single source of truth.
///
/// 2 important rules:
///   1. CLIENT SE KABHI APNA IP NAHI LENA — hamesha server ka resolved
///      RemoteIpAddress use karo. Warna koi bhi forged header bhej ke
///      doosre network ki files dekh sakta hai.
///   2. Program.cs me UseForwardedHeaders PEHLE chalna chahiye, warna
///      hosting ke proxy ke peeche sab ka IP same dikhega.
/// </summary>
public class ClientIpService : IClientIpService
{
    /// <summary>
    /// HttpContext se network id nikaalta hai.
    /// RemoteIpAddress tab sahi hota hai jab UseForwardedHeaders pehle chala ho.
    /// </summary>
    public string GetNetworkId(HttpContext context)
    {
        return Normalize(context.Connection.RemoteIpAddress);
    }

    /// <summary>
    /// IPv4 ko as-is rakhta hai. IPv6 ko /64 prefix tak trims karta hai.
    ///
    /// IPv6 me har ghar ko ek /64 block milta hai, isliye ek wifi ke saare
    /// devices ke IP ke pehle 64 bits same hote hain. Poora 128-bit compare
    /// karte to same wifi ke devices alag-alag lagte — aur app kaam hi nahi karta.
    /// </summary>
    public static string Normalize(IPAddress? address)
    {
        // Bilkul koi IP nahi mila (rare) — sab kuch ek hi bucket me daal do
        if (address is null)
        {
            return "unknown";
        }

        // Loopback ko hamesha 127.0.0.1 banao (v6 loopback alag string banata hai)
        if (IPAddress.IsLoopback(address))
        {
            address = IPAddress.Loopback;
        }

        // IPv4-mapped IPv6 (::ffff:203.0.113.5) ko asli IPv4 me khol do.
        // Ye Windows me common hai — bina iske DB me 2 alag rows ban jaate
        // jabki user ke liye dono ek hi IP hain.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // ---------------- IPv4 ----------------
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var v4 = address.GetAddressBytes();
            return $"{v4[0]}.{v4[1]}.{v4[2]}.{v4[3]}";
        }

        // ---------------- IPv6 ----------------
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var v6 = address.GetAddressBytes();

            // Link-local (fe80::/10) aur site-local (fec0::/10) me scope-id hota hai.
            // ToString() woh chhupa deta hai — warna same network ke devices
            // alag-alag string ban jayenge.
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return address.ToString();
            }

            // /48 prefix = pehle 6 bytes. /64 se zyada wide hai, isliye kuch
            // ISPs/routers jo same LAN ko alag /64 blocks me dete hain,
            // unke devices bhi same bucket me aa jaate hain.
            Span<byte> prefix = stackalloc byte[16];
            v6.AsSpan(0, 6).CopyTo(prefix);

            // Format: 2409:8a34::  (DB me 45 chars me aa jayega)
            return new IPAddress(prefix).ToString();
        }

        // Koi aur address family (future-proofing)
        return address.ToString();
    }
}