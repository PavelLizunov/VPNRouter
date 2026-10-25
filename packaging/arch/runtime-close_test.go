package tun

import (
	"errors"
	"runtime"
	"testing"

	"github.com/sagernet/sing-tun"
)

type closeOrderTun struct {
	tun.Tun
	closed bool
}

func (t *closeOrderTun) Close() error {
	t.closed = true
	return nil
}

type closeOrderStack struct {
	tun.Stack
	device *closeOrderTun
}

func (s *closeOrderStack) Close() error {
	if !s.device.closed {
		return errors.New("Linux gvisor stack closed before TUN")
	}
	return nil
}

func TestLinuxGvisorClosesTunBeforeStack(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("Linux TUN close ordering")
	}
	device := &closeOrderTun{}
	inbound := &Inbound{stack: "gvisor", tunIf: device, tunStack: &closeOrderStack{device: device}}
	if err := inbound.Close(); err != nil {
		t.Fatal(err)
	}
	if !device.closed {
		t.Fatal("TUN device was not closed")
	}
}
