// SPDX-License-Identifier: GPL-3.0-only
//go:build windows

package main

import (
	"encoding/binary"
	"errors"
	"math/bits"
	"net"
	"net/netip"
	"sync"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	ipHelper       = windows.NewLazySystemDLL("iphlpapi.dll")
	getTCPTable    = ipHelper.NewProc("GetExtendedTcpTable")
	getUDPTable    = ipHelper.NewProc("GetExtendedUdpTable")
	errOwnership   = errors.New("cannot establish local socket ownership")
	tcpTableBuffer ownerTableBuffer
	udpTableBuffer ownerTableBuffer
)

func elevated() bool {
	var value, size uint32
	err := windows.GetTokenInformation(windows.GetCurrentProcessToken(), windows.TokenElevation,
		(*byte)(unsafe.Pointer(&value)), uint32(unsafe.Sizeof(value)), &size)
	return err != nil || value != 0 // Failure to determine privilege is not permission to run.
}

func bindInterface(index int) func(string, string, syscall.RawConn) error {
	return func(network, address string, raw syscall.RawConn) error {
		var optionErr error
		err := raw.Control(func(fd uintptr) {
			switch network {
			case "tcp4", "udp4":
				// IP_UNICAST_IF requires the index in NETWORK byte order.
				optionErr = syscall.SetsockoptInt(syscall.Handle(fd), syscall.IPPROTO_IP, 31, int(bits.ReverseBytes32(uint32(index))))
			case "tcp6", "udp6":
				// IPV6_UNICAST_IF requires the index in HOST byte order.
				optionErr = syscall.SetsockoptInt(syscall.Handle(fd), syscall.IPPROTO_IPV6, 31, index)
			default:
				optionErr = errConfig
			}
		})
		if err != nil {
			return err
		}
		return optionErr
	}
}

// IP Helper ownership tables are readable without elevation. Restrict all local
// SOCKS sockets to IPv4 so only the bounded AF_INET table layouts are needed.
type ownerTableBuffer struct {
	mu    sync.Mutex
	bytes []byte
	size  uint32
}

const maxOwnerTableBytes = 8 * 1024 * 1024

// The caller holds buffer.mu through lookup. Reuse one bounded buffer per table,
// perform one native snapshot per packet, and never cache an ownership decision.
func ownerTable(proc *windows.LazyProc, class uintptr, buffer *ownerTableBuffer) ([]byte, error) {
	if len(buffer.bytes) == 0 {
		buffer.bytes = make([]byte, 4096)
	}
	for attempt := 0; attempt < 3; attempt++ {
		buffer.size = uint32(len(buffer.bytes))
		// Call SyscallN directly: LazyProc.Call's variadic wrapper otherwise allocates
		// an argument array on every packet. The pinned buffer owns the size pointer too.
		status, _, _ := syscall.SyscallN(proc.Addr(), uintptr(unsafe.Pointer(&buffer.bytes[0])), uintptr(unsafe.Pointer(&buffer.size)), 0, syscall.AF_INET, class, 0)
		if status == uintptr(windows.ERROR_INSUFFICIENT_BUFFER) {
			if buffer.size < 4 || buffer.size > maxOwnerTableBytes {
				return nil, errOwnership
			}
			buffer.bytes = make([]byte, min(maxOwnerTableBytes, max(int(buffer.size), len(buffer.bytes)*2)))
			continue
		}
		if status != 0 || buffer.size < 4 || int(buffer.size) > len(buffer.bytes) {
			return nil, errOwnership
		}
		return buffer.bytes[:buffer.size], nil
	}
	return nil, errOwnership
}

func tcpClientOwner(connection *net.TCPConn) (uint32, error) {
	tcpTableBuffer.mu.Lock()
	defer tcpTableBuffer.mu.Unlock()
	table, err := ownerTable(getTCPTable, 5, &tcpTableBuffer) // TCP_TABLE_OWNER_PID_ALL
	if err != nil {
		return 0, err
	}
	client := connection.RemoteAddr().(*net.TCPAddr).AddrPort()
	server := connection.LocalAddr().(*net.TCPAddr).AddrPort()
	return tcpOwnerFromTable(table, client, server)
}

func tcpOwnerFromTable(table []byte, client, server netip.AddrPort) (uint32, error) {
	if len(table) < 4 || !client.Addr().Is4() || !server.Addr().Is4() {
		return 0, errOwnership
	}
	count := int(binary.LittleEndian.Uint32(table))
	if count > (len(table)-4)/24 {
		return 0, errOwnership
	}
	var owner uint32
	for i := 0; i < count; i++ {
		row := table[4+i*24 : 4+(i+1)*24]
		if binary.LittleEndian.Uint32(row) != 5 || tableAddress(row[4:12]) != client || tableAddress(row[12:20]) != server {
			continue
		}
		pid := binary.LittleEndian.Uint32(row[20:24])
		if pid == 0 || owner != 0 && owner != pid {
			return 0, errOwnership
		}
		owner = pid
	}
	if owner == 0 {
		return 0, errOwnership
	}
	return owner, nil
}

func udpOwnedBy(address netip.AddrPort, pid uint32) bool {
	udpTableBuffer.mu.Lock()
	defer udpTableBuffer.mu.Unlock()
	table, err := ownerTable(getUDPTable, 1, &udpTableBuffer) // UDP_TABLE_OWNER_PID
	return err == nil && udpOwnerFromTable(table, address, pid)
}

func udpOwnerFromTable(table []byte, address netip.AddrPort, pid uint32) bool {
	if len(table) < 4 || pid == 0 || !address.Addr().Is4() {
		return false
	}
	count := int(binary.LittleEndian.Uint32(table))
	if count > (len(table)-4)/12 {
		return false
	}
	found := false
	for i := 0; i < count; i++ {
		row := table[4+i*12 : 4+(i+1)*12]
		endpoint := tableAddress(row[:8])
		if endpoint.Port() != address.Port() || (!endpoint.Addr().IsUnspecified() && endpoint.Addr() != address.Addr()) {
			continue
		}
		if binary.LittleEndian.Uint32(row[8:12]) != pid {
			return false
		}
		found = true
	}
	return found
}

func tableAddress(row []byte) netip.AddrPort {
	return netip.AddrPortFrom(netip.AddrFrom4([4]byte{row[0], row[1], row[2], row[3]}), binary.BigEndian.Uint16(row[4:6]))
}
