using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using OnionHopV3.Core.Networking;
using Xunit;

namespace OnionHopV3.Tests.Networking;

public sealed class DohNameResolverTests
{
    [Fact]
    public void BuildQuery_encodes_a_well_formed_A_question()
    {
        var query = DohNameResolver.BuildQueryForTest("example.com", 1);

        // Header: QDCOUNT == 1, RD flag set.
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal(0x01, query[2] & 0x01);

        // Question name: 7"example" 3"com" 0, then QTYPE=A(1), QCLASS=IN(1).
        var expectedName = new byte[] { 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0 };
        Assert.Equal(expectedName, query.Skip(12).Take(expectedName.Length).ToArray());

        var typeOffset = 12 + expectedName.Length;
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(typeOffset)));     // QTYPE A
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(typeOffset + 2))); // QCLASS IN
    }

    [Fact]
    public void ParseAddresses_extracts_A_and_AAAA_records()
    {
        var response = BuildResponse(
            "example.com",
            (1, IPAddress.Parse("93.184.216.34").GetAddressBytes()),
            (28, IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946").GetAddressBytes()));

        var addresses = DohNameResolver.ParseAddressesForTest(response);

        Assert.Contains(IPAddress.Parse("93.184.216.34"), addresses);
        Assert.Contains(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), addresses);
    }

    [Fact]
    public void ParseAddresses_ignores_non_address_records()
    {
        // A CNAME (type 5) answer with junk rdata must not be misread as an address.
        var response = BuildResponse("example.com", (5, new byte[] { 1, 2, 3, 4 }));
        Assert.Empty(DohNameResolver.ParseAddressesForTest(response));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01 })] // too short for a header
    [InlineData(new byte[0])]
    public void ParseAddresses_handles_malformed_input(byte[] bytes)
    {
        Assert.Empty(DohNameResolver.ParseAddressesForTest(bytes));
    }

    // Build a minimal DNS response: header + one question + the given answer records, using a
    // compression pointer (0xC00C) for each answer's name like real resolvers do.
    private static byte[] BuildResponse(string host, params (ushort Type, byte[] Rdata)[] answers)
    {
        var bytes = new List<byte>();

        // Header
        bytes.AddRange(new byte[] { 0x00, 0x00 });             // ID
        bytes.AddRange(new byte[] { 0x81, 0x80 });             // flags: response, RD, RA
        bytes.AddRange(BigEndian(1));                          // QDCOUNT
        bytes.AddRange(BigEndian((ushort)answers.Length));     // ANCOUNT
        bytes.AddRange(new byte[] { 0x00, 0x00 });             // NSCOUNT
        bytes.AddRange(new byte[] { 0x00, 0x00 });             // ARCOUNT

        // Question
        foreach (var label in host.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(label.Select(c => (byte)c));
        }
        bytes.Add(0x00);
        bytes.AddRange(BigEndian(1)); // QTYPE A
        bytes.AddRange(BigEndian(1)); // QCLASS IN

        // Answers
        foreach (var (type, rdata) in answers)
        {
            bytes.AddRange(new byte[] { 0xC0, 0x0C }); // name pointer to offset 12 (the question)
            bytes.AddRange(BigEndian(type));
            bytes.AddRange(BigEndian(1));              // CLASS IN
            bytes.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x3C }); // TTL 60
            bytes.AddRange(BigEndian((ushort)rdata.Length));
            bytes.AddRange(rdata);
        }

        return bytes.ToArray();
    }

    private static byte[] BigEndian(ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        return buffer;
    }
}
