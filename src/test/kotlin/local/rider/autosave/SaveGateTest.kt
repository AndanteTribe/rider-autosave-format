package local.rider.autosave

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SaveGateTest {
    @Test fun `unchanged successful document is skipped`() {
        val gate = SaveGate()
        assertTrue(gate.begin(1))
        gate.beginPass()
        assertTrue(gate.needsAnotherPass(1, 2))
        gate.beginPass()
        assertFalse(gate.needsAnotherPass(2, 2))
        gate.finish(2, true)
        assertFalse(gate.begin(2))
        assertTrue(gate.begin(3))
    }
    @Test fun `reentrant saves are coalesced and stable formatting converges`() {
        val gate = SaveGate()
        assertTrue(gate.begin(1))
        gate.beginPass()
        assertFalse(gate.begin(1))
        assertFalse(gate.begin(2))
        assertTrue(gate.needsAnotherPass(1, 2))
        gate.beginPass()
        assertFalse(gate.needsAnotherPass(2, 2))
        gate.finish(2, true)
        assertFalse(gate.begin(2))
    }
    @Test fun `failed formatting can be retried`() {
        val gate = SaveGate()
        assertTrue(gate.begin(1))
        gate.finish(1, false)
        assertTrue(gate.begin(1))
    }
    @Test fun `pending newer save survives a failed older operation`() {
        val gate = SaveGate()
        assertTrue(gate.begin(1))
        gate.beginPass()
        assertFalse(gate.begin(2))
        assertFalse(gate.begin(3))
        val pending = gate.finish(3, false)
        assertEquals(3L, pending)
        assertTrue(gate.begin(pending!!))
        gate.beginPass()
        assertFalse(gate.needsAnotherPass(3, 3))
        assertNull(gate.finish(3, true))
        assertFalse(gate.begin(3))
    }
    @Test fun `older success cannot complete a pending saved revision`() {
        val gate = SaveGate()
        assertTrue(gate.begin(1))
        gate.beginPass()
        assertFalse(gate.begin(2))
        assertTrue(gate.needsAnotherPass(1, 2))
        assertEquals(2L, gate.finish(2, true))
        assertTrue(gate.begin(2))
    }
    @Test fun `failure invalidates an earlier completed stamp`() {
        val gate = SaveGate()
        assertTrue(gate.begin(1))
        gate.beginPass()
        assertFalse(gate.needsAnotherPass(1, 1))
        assertNull(gate.finish(1, true))
        assertTrue(gate.begin(2))
        assertNull(gate.finish(2, false))
        assertTrue(gate.begin(1))
    }
}
