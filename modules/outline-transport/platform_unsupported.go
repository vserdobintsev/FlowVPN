// SPDX-License-Identifier: GPL-3.0-only
//go:build !windows

package main

import (
	"errors"
	"net"
	"net/netip"
	"syscall"
)

var errOwnership = errors.New("this adapter requires Windows")

func elevated() bool { return true }
func bindInterface(int) func(string, string, syscall.RawConn) error {
	return func(string, string, syscall.RawConn) error { return errOwnership }
}
func tcpClientOwner(*net.TCPConn) (uint32, error) { return 0, errOwnership }
func udpOwnedBy(netip.AddrPort, uint32) bool      { return false }
