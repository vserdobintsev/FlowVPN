// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/signal"
	"runtime/debug"
	"time"
)

func main() {
	debug.SetTraceback("none")
	if len(os.Args) == 2 && os.Args[1] == "--version" {
		fmt.Println("flowvpn-outline-transport " + moduleVersion)
		return
	}
	if len(os.Args) != 1 || elevated() {
		fmt.Fprintln(os.Stderr, "adapter must be started without elevation or arguments")
		os.Exit(2)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	if err := run(ctx, os.Stdin, os.Stdout); err != nil {
		// Never print external error strings: they can contain addresses or credentials.
		fmt.Fprintln(os.Stderr, "adapter session failed")
		os.Exit(1)
	}
}

func run(ctx context.Context, input io.ReadCloser, output io.Writer) error {
	defer input.Close()
	reader := bufio.NewReaderSize(input, maxConfigBytes+1)
	type initial struct {
		config Config
		err    error
	}
	loaded := make(chan initial, 1)
	go func() {
		line, err := reader.ReadSlice('\n')
		if err != nil {
			loaded <- initial{err: errConfig}
			return
		}
		config, err := parseConfig(line)
		clear(line)
		loaded <- initial{config, err}
	}()
	timer := time.NewTimer(10 * time.Second)
	defer timer.Stop()
	var c Config
	select {
	case item := <-loaded:
		if item.err != nil {
			return item.err
		}
		c = item.config
	case <-timer.C:
		return errConfig
	case <-ctx.Done():
		return nil
	}
	transport, err := makeOutbound(c)
	if err != nil {
		return err
	}
	server, err := newServer(c.SocksUsername, c.SocksPassword, transport)
	if err != nil {
		return err
	}
	defer server.Close()
	serveDone := make(chan error, 1)
	go func() { serveDone <- server.Serve() }()
	if err := json.NewEncoder(output).Encode(struct {
		ProtocolVersion int `json:"protocolVersion"`
		Port            int `json:"port"`
	}{protocolVersion, server.Port()}); err != nil {
		return err
	}
	ownerDone := make(chan struct{})
	go func() {
		// One configuration per process. EOF, pipe failure or unexpected further
		// input ends the session; there is no runtime command/config injection.
		_, _ = reader.ReadByte()
		close(ownerDone)
	}()
	select {
	case <-ctx.Done():
	case <-ownerDone:
	case err = <-serveDone:
		if err != nil {
			return errors.New("listener stopped")
		}
	}
	server.Close()
	select {
	case <-server.Done():
		return nil
	case <-time.After(5 * time.Second):
		return errors.New("shutdown timeout")
	}
}
