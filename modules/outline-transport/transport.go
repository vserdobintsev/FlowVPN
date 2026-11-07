// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"context"
	"net"
	"strconv"
	"time"

	"golang.getoutline.org/sdk/transport"
	"golang.getoutline.org/sdk/transport/shadowsocks"
)

const dialTimeout = 10 * time.Second

type outbound struct {
	stream transport.StreamDialer
	packet transport.PacketListener
}

func makeOutbound(c Config) (outbound, error) {
	key, prefix, err := c.validate()
	if err != nil {
		return outbound{}, err
	}
	if _, err := net.InterfaceByIndex(c.InterfaceIndex); err != nil {
		return outbound{}, errConfig
	}
	address := net.JoinHostPort(c.Server, strconv.Itoa(c.ServerPort))
	dialer := net.Dialer{Timeout: dialTimeout, KeepAlive: 30 * time.Second, Control: bindInterface(c.InterfaceIndex)}
	sd, err := shadowsocks.NewStreamDialer(&transport.TCPEndpoint{Dialer: dialer, Address: address}, key)
	if err != nil {
		return outbound{}, errConfig
	}
	if len(prefix) != 0 {
		sd.SaltGenerator = shadowsocks.NewPrefixSaltGenerator(prefix)
	}
	pl, err := shadowsocks.NewPacketListener(transport.UDPEndpoint{Dialer: dialer, Address: address}, key)
	if err != nil {
		return outbound{}, errConfig
	}
	// The general legacy Outline transport applies prefix to TCP only. Explicit
	// UDP-prefix configurations are a distinct capability and are not accepted.
	return outbound{stream: sd, packet: pl}, nil
}

func (o outbound) dial(ctx context.Context, address string) (transport.StreamConn, error) {
	deadline, cancel := context.WithTimeout(ctx, dialTimeout)
	defer cancel()
	return o.stream.DialStream(deadline, address)
}
