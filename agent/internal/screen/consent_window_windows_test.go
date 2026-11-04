//go:build windows

package screen

import "testing"

func TestConsentWindowLayoutPutsDenyLeftOfAllowInsideTheCard(t *testing.T) {
	for _, dpi := range []int32{96, 144, 192} {
		scale := func(v int32) int32 { return v * dpi / 96 }
		l := layoutConsentWindow(scale, scale(60), scale(40), scale(200))
		if l.W != scale(460) {
			t.Fatalf("dpi %d: width %d", dpi, l.W)
		}
		if l.Deny.Right > l.Allow.Left || l.Deny.Left < 0 || l.Allow.Right > l.W || l.Allow.Bottom > l.H {
			t.Fatalf("dpi %d: buttons outside the card or overlapping: %+v %+v in %dx%d", dpi, l.Deny, l.Allow, l.W, l.H)
		}
		if l.Allow.Right-l.Allow.Left < scale(200) {
			t.Fatalf("dpi %d: a long label does not fit its button", dpi)
		}
		if !(l.Title.Bottom <= l.Tech.Top && l.Tech.Bottom <= l.Message.Top && l.Message.Bottom <= l.Bar.Top && l.Bar.Bottom <= l.TimeLeft.Top &&
			l.TimeLeft.Bottom <= l.Deny.Top) {
			t.Fatalf("dpi %d: parts overlap: %+v", dpi, l)
		}
		if l.Message.Bottom-l.Message.Top != scale(60) {
			t.Fatalf("dpi %d: the message does not get its measured height", dpi)
		}
	}
}

func TestConsentWindowHitFindsTheButtons(t *testing.T) {
	l := layoutConsentWindow(func(v int32) int32 { return v }, 40, 30, 30)
	if got := l.hit(l.Deny.Left+1, l.Deny.Top+1); got != buttonDeny {
		t.Errorf("deny: %d", got)
	}
	if got := l.hit(l.Allow.Right-1, l.Allow.Bottom-1); got != buttonAllow {
		t.Errorf("allow: %d", got)
	}
	if got := l.hit(l.Allow.Right, l.Allow.Top); got != buttonNone {
		t.Errorf("right of allow: %d", got)
	}
	if got := l.hit(l.Message.Left, l.Message.Top); got != buttonNone {
		t.Errorf("message: %d", got)
	}
}
