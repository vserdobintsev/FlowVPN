// SPDX-License-Identifier: GPL-3.0-only
//go:build windows

package main

import (
	"encoding/binary"
	"net"
	"net/netip"
	"os"
	"strconv"
	"testing"
)

func TestOwnershipTableValidation(t *testing.T) {
	address := netip.MustParseAddrPort("127.0.0.1:12345")
	row := make([]byte, 12)
	copy(row, []byte{127, 0, 0, 1})
	binary.BigEndian.PutUint16(row[4:6], 12345)
	binary.LittleEndian.PutUint32(row[8:12], 42)
	table := append([]byte{1, 0, 0, 0}, row...)
	if !udpOwnerFromTable(table, address, 42) || udpOwnerFromTable(table, address, 43) {
		t.Fatal("owner PID validation failed")
	}
	if udpOwnerFromTable(table[:len(table)-1], address, 42) {
		t.Fatal("truncated table accepted")
	}
	table[0] = 2
	other := append([]byte(nil), row...)
	binary.LittleEndian.PutUint32(other[8:12], 43)
	table = append(table, other...)
	if udpOwnerFromTable(table, address, 42) {
		t.Fatal("ambiguous UDP ownership accepted")
	}
	if udpOwnerFromTable([]byte{255, 255, 255, 255}, address, 42) {
		t.Fatal("overflowing row count accepted")
	}
	if _, err := tcpOwnerFromTable([]byte{255, 255, 255, 255}, address, address); err == nil {
		t.Fatal("overflowing TCP count accepted")
	}
}

func BenchmarkUDPOwnershipNative(b *testing.B) {
	for _, ports := range []int{32, 256, 1024} {
		b.Run("ports_"+strconv.Itoa(ports), func(b *testing.B) {
			var address netip.AddrPort
			for range ports {
				connection, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
				if err != nil {
					b.Fatal(err)
				}
				b.Cleanup(func() { connection.Close() })
				address = connection.LocalAddr().(*net.UDPAddr).AddrPort()
			}
			pid := uint32(os.Getpid())
			if !udpOwnedBy(address, pid) {
				b.Fatal("ownership warm-up failed")
			}
			b.ReportAllocs()
			b.ResetTimer()
			for b.Loop() {
				if !udpOwnedBy(address, pid) {
					b.Fatal("ownership check failed")
				}
			}
			b.StopTimer()
			udpTableBuffer.mu.Lock()
			b.ReportMetric(float64(len(udpTableBuffer.bytes)), "buffer-bytes")
			udpTableBuffer.mu.Unlock()
			b.ReportMetric(float64(b.N)/b.Elapsed().Seconds(), "checks/s")
		})
	}
}

func TestUDPOwnershipSteadyStateAllocations(t *testing.T) {
	connection, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	address, pid := connection.LocalAddr().(*net.UDPAddr).AddrPort(), uint32(os.Getpid())
	if !udpOwnedBy(address, pid) {
		t.Fatal("ownership warm-up failed")
	}
	valid := true
	allocations := testing.AllocsPerRun(100, func() { valid = udpOwnedBy(address, pid) && valid })
	if !valid || allocations != 0 {
		t.Fatalf("ownership hot path: valid=%v allocations=%v", valid, allocations)
	}
}
